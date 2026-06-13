# M3.1b: Generic Decode Walk — Length-as-Computed-Output, the Foundation for Z80 Prefixes AND 8086 ModR/M

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** generalize CpuEmulator's DECODE model from "**single-byte opcode → fixed-length
descriptor**" to a **decode-function / per-unit-consumption walk** that produces
`(operation-key, COMPUTED-length, decoded-operand-info)`. Today the entire decode pipeline assumes
the length of an instruction is a static property derivable from its first byte's addressing mode:
`OpcodeDescriptor.Length` is an `int` field (`src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs:45`),
`BlockCompiler.Discover` advances PC by `pc += d.Length` (`BlockCompiler.cs:104`), the JIT table is a
`[256]` array indexed by one byte (`BlockCompiler.cs:101`, `CpuEmitter.cs:1316`), the interpreter
`Execute` is a `switch (opcode)` over that one byte (`CpuEmitter.cs:127-137`), the monitor's
`InstructionLength`/`Disassemble`/`TryAssemble` all `switch` on the one byte (`CpuEmitter.cs:1014,
1251, 1032`), and the importer derives bytes from mode alone (`OpcodeDataset.ExpectedBytes`,
`OpcodeDataset.cs:146`). This PR makes **instruction length a genuine OUTPUT of a per-unit decode
walk**, with the 6502 as the trivial degenerate case (no prefixes, no mid-stream length-determining
byte, key == the opcode byte, length == a fixed function of the consumed units).

This realizes **ADR 0001 Decision 1 option (B)** — "a generic front-end decoder consumes bytes until
it has resolved a full opcode" (`docs/architecture/0001-z80-second-architecture.md:134-143`) — and
the binding CONVERGENT constraint both forward briefs name as the single highest-leverage M3 input:
the 8086 brief §10.1 ("**make instruction length a COMPUTED OUTPUT of the decode walk, not a static
field**", `docs/research/8086-architecture-analysis.md:778-814`) and the 68000 brief §"M3 NOW" items
3-5 ("**make the fetch unit a parameter; make Length operand-computed**",
`docs/research/68000-architecture-analysis.md:745-761`).

**This is a PURE REFACTOR under the 6502.** No Z80 code is added. The entire forward value is proven
by (a) the **6502 staying perfectly green** — the full existing suite, the `CPUEMULATOR_UAT=full`
TomHarte sweep through BOTH tiers (1.51M cases each), and Klaus cycle-exact (96,241,367, both tiers)
unchanged — and (b) a **synthetic generator/JIT test CPU** whose spec exercises ALL THREE decoder
properties the briefs name (a PREFIX byte; a multi-byte opcode; a **length-determining mid-stream
byte** — a ModR/M-like byte whose value sets how many MORE bytes follow → variable computed length;
and a **sub-field key** — an operation selected by bits of a non-first byte). The synthetic CPU is
the M3 thesis made testable: it proves the decoder generalizes to Z80 prefixes AND 8086 ModR/M
*before either CPU exists*.

**PR:** branch `feat/m3-generic-decoder` (base `main`, head `3d1eff4` — the M3.1a merge;
**~1436 tests green** is the baseline — report the exact count at Task 0). This plan file is a
preparatory doc commit on that branch; the implementation tasks follow.

---

## Scope

**IN scope (the decode dimension, end to end):**

1. **A `DecodeResult` value type + an `IDecoder` decode-walk abstraction** (Ground truth A) shared by
   the four decode sites — interpreter `Step`, JIT `Discover`, the disassembler, and the assembler —
   so there is **one decode model, not four `switch(opcode)` sites**. The walk consumes fetch units
   (bytes by default) over a generated decode table and returns
   `DecodeResult(OperationKey, Length, Operands)` where `Length` is a **computed output of the walk**.

2. **`OpcodeDescriptor.Length` (static `int` field) → length computed by the decode walk**
   (Ground truth B). The descriptor stops carrying a fixed `Length`; instead it carries the data the
   walk needs to *compute* length (a "length rule" — for the 6502 a constant per mode; for the
   synthetic CPU a function of a consumed mid-stream byte). `Discover`/`Step` advance PC by the
   decoder's **returned** length, never a static field.

3. **The opaque operation-key model** (Ground truth C): a `DecodeResult.OperationKey` computable from
   whatever bits/bytes the walk consumes — supporting **non-first-byte sub-fields** (the 8086
   `opcode<<3 | modrm.reg` opcode-group case). For the 6502 the key IS the opcode byte (degenerate).

4. **The fetch unit parameterized** (Ground truth D): byte default; the decode walk reads through a
   `IFetchStream` whose unit is configurable (byte for 6502/Z80/8086; word-capable for the 68000),
   so the walk never hardcodes `Read8`.

5. **The generated decode table + decode function** (Ground truth E): the generator emits, per spec,
   a `Decode(...)` walk (replacing the four `switch(opcode)` sites with one) and the data it consults.
   For the 6502 this is a single-byte-key, fixed-length walk producing byte-identical *behavior*.

6. **The synthetic multi-byte / ModR/M-like test CPU** (Ground truth F): a generator/JIT fixture whose
   spec declares a PREFIX byte, a multi-byte opcode, a length-determining mid-stream byte, and a
   sub-field key — exercising all three decoder properties the 8086 brief §10.1 names. Modeled on the
   M3.1a `SyntheticRegisterSetTests` precedent (`tests/CpuEmulator.Tests/Generators/SyntheticRegisterSetTests.cs`).

**NOT in scope (stated so an implementer does not reach for it):**

- **ANY Z80 code.** No Z80 spec, no `CB`/`ED`/`DD`/`FD` prefix bytes, no `DDCB dd op` compound forms,
  no Z80 register pairs/alternate set, no Z80 interrupt modes. The forward value is proven by the 6502
  staying green + the synthetic CPU, exactly as the brief mandates ("Scope = 6502-only refactor + a
  SYNTHETIC test CPU. NO Z80 code"). The Z80's prefix tables are M3.3 (dataset) / M3.4 (interpreter).
- **ANY 8086 code.** No segmentation, no real ModR/M effective-address computation, no `seg<<4+offset`
  math, no 20-bit space, no `d`/`w`-bit operand resolution. The synthetic CPU's "ModR/M-like" byte is a
  *length-determining* byte only — it proves the walk can compute length from a mid-stream byte; it
  does NOT compute a real 8086 EA. (8086 brief §10.3: "do not pre-build segmentation, ModR/M, or the
  20-bit space in M3", `8086-architecture-analysis.md:833-841`.)
- **The flag model** (`Flag` enum, `s_flagMembers`, the per-arch flag micro-op family). Untouched —
  it is a separate M3 chunk, as M3.1a recorded (`m3a-register-file.md:67-70`).
- **16-bit register ARITHMETIC** (`ADD HL,rr`, etc.) and **register aliasing** (`B`/`C` ⊂ `BC`).
  Those are M3.4 (Z80 interpreter). This plan is decode-shape only.
- **JIT genericity J1** (making `BlockCompiler`/`BlockDelegate`/`JittedCpu` generic over the CPU
  type — retiring `typeof(Mos6502Cpu)`). J3 (decode-driven discovery) is done here; J1 (CPU type as
  data) stays deferred to M3.5. `Discover` still compiles against `Mos6502Cpu.JitDescriptors`; only
  *how it computes length and resolves the operation* changes.
- **Real word-granular fetch for a shipped CPU.** The fetch unit is *parameterized* (the abstraction
  exists and is exercised by a synthetic word-unit test), but no shipped CPU uses word fetch in M3 —
  the 68000 (M4) is the first word-fetch consumer. We build the seam and prove it, not a 68000.
- **The cycle model** (`BaseCycles`, `PageCrossPenalty`). Untouched. ADR J5 (`0001-…:509`) flags these
  as 6502-shaped, but cycle generalization is a separate concern from decode length; this plan does
  not touch cycle charging. (Recorded deviation below explains why `BaseCycles` stays a field while
  `Length` becomes computed.)

**Recorded deviations / departures this plan makes deliberately:**

- **`Length` becomes computed but `BaseCycles`/`PageCrossPenalty` stay descriptor fields.** The brief
  scopes the *length* generalization (decode walk), not the *cycle* generalization (ADR J5, a later
  chunk). Length and cycles are genuinely separable: length is "how far PC advances" (a decode-walk
  output the 8086's ModR/M makes data-dependent); cycles is "how long the instruction takes" (a
  timing-model output the Z80 M-cycle / 68000 operand-dependent timing makes data-dependent). This
  plan generalizes ONLY length. `BaseCycles` stays a static field; `Discover` keeps reading it for the
  block budget. Recorded: this is a deliberate scope line, not an oversight — J5 is its own future
  chunk and conflating it here would balloon the PR. The synthetic CPU asserts length is computed; it
  does NOT assert anything new about cycles.

- **The operation-key is an opaque `uint`, not a struct or a raw-byte concatenation.** The brief says
  "opaque operation-key (not concatenated raw bytes)." I choose a `uint OperationKey` packed by the
  generated decode function (for the 6502: `key == opcode`; for the synthetic prefix CPU:
  `key == (prefix << 8) | opcode`; for the synthetic sub-field CPU: `key == (opcode << 3) | subfield`).
  Rationale: a `uint` is (a) a cheap dictionary/array index in the hot interpreter loop — the ADR's
  Decision 1 cons for option (B) was "a dictionary lookup per instruction is slower than an array
  index" (`0001-…:141`), and a `uint` key lets the 6502 stay a dense `[256]` array index (zero hot-path
  regression) while a prefixed CPU uses a small dictionary; (b) trivially equatable for the generator's
  incremental cache; (c) able to encode a non-first-byte sub-field (property 3) by construction — the
  generated `Decode` function decides which bits go where. A struct or string key buys no validation
  and costs hot-path speed. **The key is "whatever the generated decode function computes," not the
  bytes** — exactly the 68000 brief's requirement (`68000-architecture-analysis.md:751-755`). Recorded.

- **The 6502's generated decode walk produces a byte-identical `JitDescriptors` table EXCEPT the
  `Length` field moves off `OpcodeDescriptor`.** This is a real, enumerated text change to a generated
  artifact (characterized exactly in Ground truth E, like M3.1a characterized its `JitOp` index→name
  diff). The 6502's *behavior* is byte-identical (same PC advancement, same cycles, same TomHarte/Klaus
  results); the generated descriptor *text* changes shape in one enumerated region. Stated honestly,
  gated by a re-snap.

- **`IDecoder`/`DecodeResult` live in `CpuEmulator.Core`, not the generated CPU assembly.** The
  abstraction is CPU-agnostic data the JIT (`CpuEmulator.Jit`) and the interpreter both consume, so it
  belongs in Core alongside `OpcodeDescriptor`. The generated CPU class implements/configures it (it
  emits the per-spec decode table + the concrete walk parameters), but the *types* are Core types — the
  same altitude as `OpcodeDescriptor` and `JitOp` today (`OpcodeDescriptor.cs:1`, `namespace
  CpuEmulator.Core.Jit`). Recorded.

**ADR + brief links:**
- **ADR 0001 Decision 1** (`docs/architecture/0001-z80-second-architecture.md:98-179`) — the prefix
  decode decision; option (B) (the multi-byte-key state machine) was the path the 2026-06-13 human
  checkpoint locked in (`0001-…:658-673`, "a generic multi-byte-key decoder"). Discovery must advance
  by "the decode function's total length, not a single `d.Length`" (`0001-…:166-168, 507`).
- **8086 brief §3 + §10.1** (`docs/research/8086-architecture-analysis.md:242-376, 778-814`) — the
  DESIGN SPEC for the decoder shape: length is a computed output; the walk has a length-determining
  mid-stream (ModR/M) byte; the key can include a sub-field of a non-first byte (`opcode<<3|reg`).
- **68000 brief §3 + §"M3 NOW" items 3-5** (`docs/research/68000-architecture-analysis.md:255-336,
  745-761`) — the fetch unit must be a PARAMETER (byte vs word); the key is an opaque value from a
  decode function, not the bytes; `Length` is operand-computed.
- **M3.1a plan** (`docs/superpowers/plans/2026-06-13-m3a-register-file.md`) — house style + the
  `SyntheticRegisterSetTests` precedent this plan's synthetic CPU mirrors; M3.1a explicitly deferred
  M3.1b ("the opcode stays a single byte … M3.1b is the decode dimension", `m3a-…:61-66`).

**Plan series:** M3.0 ADR ✅ · M3.1a: register file ✅ (merged, head `3d1eff4`) · **M3.1b: this plan
(decode walk) — the second framework refactor** · M3.2: bus/interrupt seams · M3.3: extraction loaders
+ Z80 dataset · M3.4: Z80 interpreter + TomHarte · M3.5: Z80 through JIT + J1 + the genericity findings.

---

## Derived numbers (verified against the repo, not assumed)

- **Baseline test count: ~1436** (stated by the brief; confirm the EXACT number at Task 0 with a clean
  `dotnet test` and record it — the estimate below is relative to the confirmed baseline). Per-task
  new-test estimate (theory rows counted individually per house convention) is tabulated under each
  task and summed in the self-review; **the headline estimate is ~1436 + ~30 ≈ ~1466.**
- **Klaus cycle anchor: 96,241,367** — a PURE-REFACTOR invariant, BOTH tiers. The decode-walk change
  touches NO cycle logic (`BaseCycles`/`PageCrossPenalty`/`ComputeCycles` are untouched — recorded
  deviation). Klaus under the interpreter AND under the JIT must reach `$3469` at EXACTLY 96,241,367
  cycles, unchanged.
- **TomHarte full sweep: 1.51M cases per tier, both tiers, 0 parity failures** — unchanged. The
  refactor changes *how* length is computed (a walk vs a field read), not *what* it computes for the
  6502 (the walk returns the same length the field held). Any divergence is a refactor bug by
  definition.
- **Generated 6502 `.g.cs` delta:** the `Decode` function is NEW (replaces the four inline
  `switch(opcode)` decode sites with one generated walk); the `JitDescriptors` rows lose the `Length`
  positional arg (it moves into the walk's length rule). `InstructionLength`/`Disassemble`/`TryAssemble`
  switch to *calling the walk* rather than their own opcode switches. Enumerated exactly in Ground
  truth E; gated by the generator snapshot re-snap.
- **Decode-site inventory (the four `switch(opcode)` that collapse to one walk):**
  | Site | File:line | What it does today | After |
  |---|---|---|---|
  | Interpreter `Step` + `Execute` | `CpuEmitter.cs:98-137` | `ReadBus(PC); PC++; switch(opcode)` → per-op method | `Step` calls the walk to get the key + length; dispatches on the key |
  | JIT `Discover` | `BlockCompiler.cs:95-107` | `Read8(pc); JitDescriptors[opcode]; pc += d.Length` | runs the walk; advances by `result.Length` |
  | Disassembler | `CpuEmitter.cs:1247-1294` | `switch(opcode)` formatting | walk resolves the operation-key; formats by key |
  | `InstructionLength` | `CpuEmitter.cs:1011-1025` | `switch(opcode)` → `ModeLength(mode)` | walk returns `result.Length` |
  | `TryAssemble` | `CpuEmitter.cs:1027+` | mnemonic→bytes (the decode *inverse*) | unchanged in direction; emits the bytes the walk would consume |
- **The four notions of "length" that converge:** today length lives in FOUR places, all 6502-mode-
  derived: (1) the interpreter's *implicit* per-mode PC advancement inside each `Op{XX}()` body (each
  mode template does its own `ReadBus(PC); PC++`); (2) `OpcodeDescriptor.Length` (`OpcodeDescriptor.cs:45`);
  (3) `InstructionLength`/`ModeLength` (`CpuEmitter.cs:999-1007`); (4) the importer's `ExpectedBytes`
  (`OpcodeDataset.cs:146`). The walk makes (2)/(3) ONE computation; (1) stays per-template for the
  interpreter bodies (the walk computes the *total* the JIT/monitor need, the bodies still advance PC
  as they read — they agree because the walk's length rule mirrors what the bodies consume — pinned by
  a cross-check, Task 5); (4) the importer is touched only to not *forbid* a computed length (Task 6).

---

## Ground truth A — the `DecodeResult` value type + the decode-walk contract

**The decode walk in one sentence:** a `Decode(IFetchStream)` function consumes fetch units (bytes by
default) over the spec's generated decode table — `consume prefixes → consume opcode →
(opcode-says-operand-byte?) consume a possibly-length-determining mid-stream byte → consume disp →
consume imm` — and returns a `DecodeResult(OperationKey, Length, Operands)` where **`Length` is the
total number of bytes the walk consumed, computed during the walk, not read from a slot.**

### A.1 The `DecodeResult` type (new, in `CpuEmulator.Core.Jit`)

```csharp
namespace CpuEmulator.Core.Jit;

/// <summary>The output of one decode walk: the operation selected, how many bytes the
/// instruction occupies (COMPUTED by the walk — NOT a static descriptor field), and the
/// decoded operand bytes the consumers (interpreter dispatch / disassembler) need.
///
/// The 6502 is the degenerate case: OperationKey == the single opcode byte, Length == a
/// fixed function of the opcode's addressing mode (1/2/3), and Operands carries the 0/1/2
/// operand bytes the mode's bus pattern reads. A prefixed CPU (Z80) packs prefix+opcode into
/// OperationKey and Length counts prefix+opcode+disp+imm. A ModR/M CPU (8086) computes Length
/// from a mid-stream byte and packs (opcode &lt;&lt; 3 | modrm.reg) into OperationKey for the
/// opcode-group encodings. The walk decides; this struct only carries the result.</summary>
public readonly record struct DecodeResult(
    uint OperationKey,   // opaque — "whatever bits/bytes select the operation" (Ground truth C)
    int Length,          // COMPUTED OUTPUT: total bytes consumed by the walk (Ground truth B)
    DecodedOperands Operands);

/// <summary>The operand bytes the walk consumed, in a fixed-capacity inline buffer (no
/// allocation in the hot loop). For the 6502 this is operandLo/operandHi (the 0/1/2 bytes the
/// disassembler + interpreter already use, CpuEmitter.cs:1251 takes operandLo/operandHi). For a
/// ModR/M CPU it additionally carries the modrm byte + the disp/imm bytes the walk consumed.
/// M3.1b keeps this minimal (Lo/Hi + a Count) — the 6502 needs exactly that; the synthetic CPU's
/// length-determining byte is carried in walk-local state and surfaced only as Length. Wider
/// operand carriage (the 8086's full disp/imm) is M5 work — the shape is extensible (a fixed
/// inline tuple) but M3 fills only what the 6502 + synthetic CPU need.</summary>
public readonly record struct DecodedOperands(byte Lo, byte Hi, byte Count)
{
    public static readonly DecodedOperands None = new(0, 0, 0);
}
```

> **Why `DecodedOperands` is minimal, not a full ModR/M carrier.** The brief is explicit that M3 does
> NOT implement real ModR/M EA computation (8086 brief §10.3, `8086-…:833-841`). The 6502
> disassembler/interpreter need exactly `operandLo`/`operandHi` (the bytes after the opcode) —
> `Disassemble(byte opcode, byte operandLo, byte operandHi)` (`CpuEmitter.cs:1251`). So
> `DecodedOperands` carries `Lo`/`Hi` + a `Count` (0/1/2 for the 6502) — enough for byte-identical 6502
> behavior. The synthetic CPU's length-determining byte affects **Length** (the property under test)
> and is surfaced as `Count`/`Length`; it does not need a real EA. When M5 lands real ModR/M,
> `DecodedOperands` grows a disp/imm tail — additive, out of M3 scope. **M3 proves the *length*
> property, not operand carriage.** Recorded.

### A.2 The `IDecoder` / `IFetchStream` contract (new, in `CpuEmulator.Core.Jit`)

```csharp
namespace CpuEmulator.Core.Jit;

/// <summary>A unit-granular fetch stream the decode walk reads through. Default unit = byte
/// (6502/Z80/8086). Word-capable for the 68000 (M4) — the walk never hardcodes Read8 (Ground
/// truth D). NextUnit advances the cursor by one unit and returns it zero-extended to a uint;
/// PeekUnit reads without advancing (the walk peeks the prefix/opcode to decide the table).</summary>
public interface IFetchStream
{
    /// <summary>Bytes per unit: 1 (byte-granular) or 2 (word-granular). The walk multiplies its
    /// unit count by this to get a byte Length.</summary>
    int UnitBytes { get; }

    /// <summary>Read the unit at the current cursor and advance the cursor by one unit.</summary>
    uint NextUnit();

    /// <summary>Read the unit at the current cursor WITHOUT advancing (lookahead).</summary>
    uint PeekUnit();

    /// <summary>How many units have been consumed so far (× UnitBytes = byte length).</summary>
    int UnitsConsumed { get; }
}

/// <summary>The generated per-CPU decode walk. Run from a stream positioned at an instruction's
/// first unit; consumes prefix/opcode/operand units per the spec's decode structure and returns
/// the (key, computed-length, operands) triple. ONE decode model the interpreter Step, the JIT
/// Discover, the disassembler, and InstructionLength all call — not four switch(opcode) sites.</summary>
public interface IDecoder
{
    DecodeResult Decode(IFetchStream stream);
}
```

### A.3 The two concrete `IFetchStream`s M3 ships

1. **`BusFetchStream`** (in `CpuEmulator.Jit` / Core) — reads through an `IAddressSpace` at a PC, the
   byte-granular default. This is what the interpreter `Step` and JIT `Discover` use today via
   `_bus.Read8(pc)` (`BlockCompiler.cs:100`), now wrapped so the walk reads units, not raw bytes. For
   the 6502 `UnitBytes == 1`, `NextUnit` == `Read8(pc++)`. Carries a `SeekTo(pc)` so `Discover` can
   reposition between instructions.
2. **`BufferFetchStream`** (test/monitor) — reads from a `ReadOnlySpan<byte>`/`byte[]`. The
   disassembler + `InstructionLength` use this (they are handed the instruction's bytes by the
   monitor, not a live bus). For the 6502 the monitor already passes `operandLo`/`operandHi`; the
   buffer stream is the same data behind the `IFetchStream` shape. Carries a `UnitBytes` ctor arg so
   the word-unit micro-proof (Ground truth D / F.2) can set `UnitBytes == 2`.

> **The contract that makes length COMPUTED, not read.** `DecodeResult.Length` is set to
> `stream.UnitsConsumed * stream.UnitBytes` at the end of the walk — i.e. **it is whatever the walk
> consumed**. The 6502 walk consumes 1 (opcode) + (0/1/2 operand bytes per the opcode's mode); the
> synthetic ModR/M-like walk consumes 1 (opcode) + 1 (the length-determining byte) + (a count the
> byte's value dictates). **Neither reads a `Length` field.** This is the load-bearing property the
> 8086 brief §10.1 demands (`8086-…:789-796`): "Build the decoder so length is computed by consuming
> bytes, and let the 6502 be the easy case of that machine."

---

## Ground truth B — `OpcodeDescriptor.Length` becomes a length RULE, not a value

**Today (`src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs:40-50`):**

```csharp
public sealed record OpcodeDescriptor(
    byte Opcode,
    string Mnemonic,
    JitMode Mode,
    JitOpClass Class,
    int Length,                 // 1-3, the InstructionLength value (discovery advances PC by this)  ← THIS
    int BaseCycles,
    bool PageCrossPenalty,
    bool NeedsFallback,
    bool EndsBlock,
    ImmutableArray<JitOp> Ops);
```

`BlockCompiler.Discover` reads it directly: `pc = unchecked((ushort)(pc + d.Length))`
(`BlockCompiler.cs:104`); `PagesSpanned` walks `d.Length` bytes (`BlockCompiler.cs:148`).

**After — the descriptor carries a `LengthRule`, the walk computes the value:**

The static `int Length` field is **removed** from `OpcodeDescriptor`. In its place the descriptor
carries the data the walk needs to compute length. For the 6502 length is a constant per mode, so the
descriptor carries an explicit **`LengthRule`** + the per-mode constant:

```csharp
/// <summary>How the decode walk computes this instruction's byte length. The 6502 is Fixed
/// (length is a constant per addressing mode — the degenerate case). A length-determining
/// mid-stream byte (the 8086 ModR/M case, the synthetic CPU's proof) is ModRmDetermined: the
/// walk reads one more byte and that byte's value sets how many MORE follow. This enum is the
/// seam that lets Length be a genuine computation while the 6502 stays trivially Fixed.</summary>
public enum LengthRule
{
    Fixed,            // length = FixedLength (6502: 1/2/3 per mode)
    ModRmDetermined,  // length = base + f(the next consumed byte) — the synthetic/8086 case
}

public sealed record OpcodeDescriptor(
    byte Opcode,
    string Mnemonic,
    JitMode Mode,
    JitOpClass Class,
    LengthRule LengthRule,      // ← REPLACES the static int Length
    int FixedLength,            // ← used when LengthRule == Fixed (the 6502's per-mode constant)
    int BaseCycles,             // UNCHANGED (cycle model out of scope — recorded deviation)
    bool PageCrossPenalty,      // UNCHANGED
    bool NeedsFallback,
    bool EndsBlock,
    ImmutableArray<JitOp> Ops);
```

> **Why keep a `FixedLength` field if the walk computes length?** For `LengthRule.Fixed` (every 6502
> opcode), the "computation" is "consume the operand units the mode implies, return `UnitsConsumed`."
> The generator already computes the per-mode constant via `ModeLength` (`CpuEmitter.cs:999`); storing
> it as `FixedLength` is the walk's *input* for the easy case. The walk for a Fixed opcode does: read
> opcode → look up descriptor → consume `FixedLength - 1` operand units → `Length = FixedLength`.
> **This is still a computation of the walk** (the walk consumes the bytes; `Length == UnitsConsumed`),
> with `FixedLength` as the per-mode parameter. For `LengthRule.ModRmDetermined` the walk reads the
> mid-stream byte and computes length from its value; `FixedLength` carries the *base* (before the
> variable tail). **The 6502 never exercises `ModRmDetermined`; the synthetic CPU does.** This keeps
> the 6502 dense-array hot path identical (descriptor lookup + a constant) while the *mechanism* is the
> general walk. Honest framing: `FixedLength` is the walk's input for the easy case, not a bypass.

**`Discover` after the change (`BlockCompiler.cs:95-107`):**

```csharp
public List<(ushort Pc, OpcodeDescriptor D, int Length)> Discover(ushort pc)
{
    var run = new List<(ushort, OpcodeDescriptor, int)>();
    var stream = new BusFetchStream(_bus, pc);          // byte-granular, positioned at pc
    for (int i = 0; i < _opts.BlockLengthCap; i++)
    {
        DecodeResult r = _decoder.Decode(stream);       // the walk — computes key + length
        OpcodeDescriptor d = Mos6502Cpu.DescriptorFor(r.OperationKey);  // 6502: key == opcode → [256] index
        run.Add((pc, d, r.Length));
        if (d.EndsBlock) break;
        pc = unchecked((ushort)(pc + r.Length));         // advance by the COMPUTED length, not d.Length
        stream.SeekTo(pc);                               // reposition the stream at the next instruction
    }
    return run;
}
```

> **The one subtlety: the 6502 descriptor lookup stays `[256]`-by-byte.** For the 6502 the walk
> produces `OperationKey == opcode` (≤ 0xFF), so `DescriptorFor(key)` is `JitDescriptors[(byte)key]` —
> the identical dense-array index it is today (`BlockCompiler.cs:101`), **zero hot-path regression**. A
> prefixed CPU produces a `uint` key > 0xFF and `DescriptorFor` uses a generated dictionary / per-page
> array (Ground truth C/E); the 6502 never takes that path. `PagesSpanned` (`BlockCompiler.cs:143-151`)
> now reads the run tuple's stored `Length` (the computed value) instead of `d.Length` — the only other
> `d.Length` reader, mechanically updated. The run tuple grows a `Length` member so both `Compile`'s
> budget-check successor-PC math (`BlockCompiler.cs:129, 136`) and `PagesSpanned` read the computed
> length, not a field.

---

## Ground truth C — the opaque operation-key model

**The contract:** `DecodeResult.OperationKey` is a `uint` the **generated decode function** computes
from whatever units/bits it consumed. It is NOT "the concatenated raw bytes" — the generated function
decides the packing, which is what lets it encode a non-first-byte sub-field.

The three key shapes M3 must support (6502 degenerate + the two synthetic proofs):

| CPU | Key shape | Why |
|---|---|---|
| **6502** (degenerate) | `key = opcode` (≤ 0xFF) | single byte selects the operation; dense `[256]` index unchanged |
| **synthetic prefix CPU** | `key = (prefix << 8) \| opcode` | a prefix byte switches "tables" — the multi-byte key (property 1+2) |
| **synthetic sub-field CPU** | `key = (opcode << 3) \| subfield` | the operation is selected by *bits of a non-first byte* (property 3 — the 8086 `opcode<<3\|modrm.reg` case) |

```csharp
/// <summary>The operation-key packing, declared by the spec's decode structure and realized by
/// the generated Decode function. The 6502 declares KeyShape.OpcodeByte (key == opcode). A
/// prefixed CPU declares PrefixedOpcode (prefix in the high bits). A sub-field CPU declares
/// OpcodeGroup (a sub-field of a non-first byte refines the opcode). The key is OPAQUE to the
/// consumers — they index a table with it; only the generated Decode function knows the packing.</summary>
public enum KeyShape { OpcodeByte, PrefixedOpcode, OpcodeGroup }
```

> **How the consumers stay key-agnostic.** The interpreter `Step`, JIT `Discover`, disassembler, and
> `InstructionLength` all receive a `uint OperationKey` and use it as a lookup index. They never parse
> it — the *generated* `Decode` knows that `(prefix << 8) | opcode` means "prefix page `prefix`, opcode
> `opcode`," and the *generated* descriptor table is keyed to match. For the 6502 the lookup is the
> dense array (key fits a byte); for a prefixed CPU the generator emits a `Dictionary<uint,
> OpcodeDescriptor>` (or per-page arrays) keyed on the same packing. **The key model is "an opaque uint
> the walk computes and the table is keyed to," supporting non-first-byte sub-fields** — exactly the
> brief's property 3. The synthetic sub-field CPU proves a key built from bits of a *non-first* byte
> resolves correctly.

**The lookup abstraction (so the 6502 array and the prefixed dictionary are one call site):**

```csharp
/// <summary>Resolve an operation-key to its descriptor. The generated CPU provides this; the
/// 6502 implementation is `JitDescriptors[(byte)key]` (the dense array); a prefixed CPU's is a
/// dictionary lookup. The JIT/interpreter call this, never the raw table — so the key packing is
/// fully encapsulated in the generated code.</summary>
public static OpcodeDescriptor DescriptorFor(uint operationKey);   // generated static method
```

---

## Ground truth D — the fetch-unit parameterization (byte default; word-capable)

**The contract:** the decode walk reads through `IFetchStream` (Ground truth A.2), whose `UnitBytes`
is 1 (byte) by default and 2 (word) for a word-granular CPU. The walk computes byte-length as
`UnitsConsumed * UnitBytes`. **The walk never calls `Read8` directly** — it calls `stream.NextUnit()`.

For the 6502 (and Z80, 8086 later) `UnitBytes == 1` and `NextUnit` returns one byte. For the 68000 (M4)
`UnitBytes == 2` and `NextUnit` returns a 16-bit word. M3 ships only the byte-granular
`BusFetchStream`/`BufferFetchStream`, but the *abstraction* is unit-granular so M4 adds a
`WordFetchStream` without reshaping the walk — the 68000 brief §"M3 NOW" item 3 requirement
(`68000-…:747-750`: "Make the decode walk's fetch unit a parameter, not a hardcoded `Read8`").

> **What M3 PROVES vs. DEFERS on the fetch unit.** M3 builds the `IFetchStream.UnitBytes` seam and a
> synthetic test asserts a `UnitBytes == 2` word stream feeds the walk and produces a byte-length that
> is `2 × units` (Ground truth F.2's word-unit micro-proof). M3 does NOT ship a word-granular shipped
> CPU (no 68000). This is the same "build the seam, prove it with a synthetic, defer the real consumer"
> discipline M3.1a used for the register file (a synthetic `BC`/`HL` CPU, no real Z80). The seam is the
> deliverable; the 68000 is M4.

**The generated decode walk's fetch-unit parameter.** The generator emits the walk with the spec's
declared fetch unit baked in. The 6502 declares (implicitly — the default) byte granularity, so its
generated walk reads bytes. A spec could declare `FetchUnit = Word` (the 68000 will), and the
generator emits a walk over `UnitBytes == 2`. M3 wires the *plumbing* (the model carries a `FetchUnit`,
defaulting to `Byte`; the generated walk + `BusFetchStream` consult it) and proves it with the
synthetic word micro-test; no shipped spec sets `Word` in M3.

---

## Ground truth E — the generated-output delta (what changes, what stays byte-identical-in-behavior)

**The honest statement: the 6502's generated `Mos6502Cpu.g.cs` gains a NEW `Decode` walk + a
`DescriptorFor` resolver, and the four decode sites change to call them; the `JitDescriptors` rows
change shape (lose the positional `Length`, gain `LengthRule, FixedLength`); the 6502's BEHAVIOR (PC
advancement, cycles, disasm text, TomHarte/Klaus results, both tiers) is byte-identical.**

| Generated/committed artifact | Changes? | What | Gated by |
|---|---|---|---|
| `Mos6502Cpu.g.cs` — NEW `Decode(IFetchStream)` walk | **YES (new method)** | the single generated decode walk; for the 6502 a byte-key fixed-length walk | generator snapshot re-snap (Task 5) |
| `Mos6502Cpu.g.cs` — NEW `DescriptorFor(uint)` | **YES (new method)** | the key→descriptor resolver; 6502 body is `JitDescriptors[(byte)key]` | the snapshot |
| `Mos6502Cpu.g.cs` — `Step`/`Execute` | **YES** | `Step` calls the walk for key+length; `Execute` dispatches on the resolved key (still a `switch`, now over the key not a raw fetched byte — for the 6502 they are equal) | the snapshot + unchanged TomHarte/Klaus |
| `Mos6502Cpu.g.cs` — `JitDescriptors` rows | **YES** | `Length: N` positional → `LengthRule.Fixed, FixedLength: N` | the snapshot re-snap |
| `Mos6502Cpu.g.cs` — `InstructionLength` | **YES (body)** | from `switch(opcode)=>ModeLength` to "run the walk / `DescriptorFor(opcode).FixedLength`" | the snapshot + monitor tests |
| `Mos6502Cpu.g.cs` — `Disassemble` | **MINIMAL (body)** | still a `switch` over the operation-key; for the 6502 key==opcode so the arms are textually identical; signature UNCHANGED (see note) | the snapshot + disasm tests |
| `Mos6502Cpu.g.cs` — state fields, `Op{XX}()` bodies, `GetRegister`/`SetRegister`, `TryAssemble` | **NO** (byte-identical) | the per-op bodies still do their own `ReadBus(PC); PC++`; the walk computes the *total* the JIT/monitor need | the existing emission suite + a Task 5 spot pin |
| `OpcodeDescriptor.cs` (Core type) | **YES** | `int Length` → `LengthRule, int FixedLength`; new `DecodeResult`/`DecodedOperands`/`IDecoder`/`IFetchStream`/`KeyShape`/`LengthRule` types | the Core unit tests + everything downstream |
| `Mos6502Spec.cs` (importer output) | **NO** | the spec DSL is unchanged — 6502 opcodes are still single bytes; the decode structure is the default (no prefixes) | `RegeneratedSpecTests` byte-equality anchor (unchanged) |
| `mos6502-opcodes.json` / importer data | **NO** | 6502 rows unchanged | unchanged |
| Klaus cycle count / TomHarte case results, BOTH tiers | **NO** (pure refactor) | the unchanged Klaus + TomHarte sweeps | the sweeps |

> **The `Disassemble`/`InstructionLength` signature note.** Today `Disassemble(byte opcode, byte
> operandLo, byte operandHi)` and `InstructionLength(byte opcode)` are the `IMonitorSupport` contract
> (`CpuEmitter.cs:1212-1217`). The migration keeps these signatures (the monitor passes the bytes it
> has) and *internally* routes through the walk over a `BufferFetchStream([opcode, operandLo,
> operandHi])` to get the key + length, then formats/returns. **This keeps `IMonitorSupport` stable (no
> Core contract churn for the monitor) while routing through the one walk.** For the 6502,
> `InstructionLength(opcode)` == `DescriptorFor(opcode).FixedLength` (every 6502 op is
> `LengthRule.Fixed`), so the generated body has a Fixed fast path. Recorded: the `IMonitorSupport`
> signatures are UNCHANGED; only their bodies route through the walk.

**Why the 6502's behavior is byte-identical.** For every 6502 opcode `LengthRule == Fixed` and
`FixedLength == ModeLength(mode)` — the exact value `OpcodeDescriptor.Length` held before. The walk
for a Fixed opcode consumes `FixedLength` bytes and returns `Length == FixedLength`. So `Discover`
advances PC by the identical amount; the interpreter bodies advance PC identically (unchanged); the
disassembler formats identically (key == opcode); `InstructionLength` returns identically. **The only
thing that changed is the *mechanism* (a walk vs a field read), not the *result*.** TomHarte (both
tiers) and Klaus (both tiers) are the proof — any divergence is a refactor bug.

---

## Ground truth F — the synthetic test CPU + its three-property mapping

**The synthetic CPU is the M3 thesis made testable.** Modeled on the M3.1a precedent
(`tests/CpuEmulator.Tests/Generators/SyntheticRegisterSetTests.cs`), it is a GENERATOR/JIT fixture —
NOT a shipped CPU, NOT the Z80, NOT the 8086. Its spec declares a decode structure the 6502 never had,
and the tests assert the generated `Decode` walk computes the right key + length for each.

### F.1 The synthetic decode-test spec

To declare prefixes / mid-stream-length-bytes / sub-field keys, the spec gains an optional
**`DecodeStructure`** declaration + `Insn` overloads (Ground truth G / Task 2 define the surface). The
6502 declares none (the default), so the 6502 spec is unchanged. The synthetic spec declares all three:

```csharp
[CpuSpecification("decodetest")]
public static class DecodeTestSpec
{
    public static readonly RegisterDef[] Registers =
    [
        new("A", 8),
        new("PC", 16, RegisterRole.ProgramCounter),
    ];

    // Decode structure: one prefix byte (0xCB); 0x80 carries a length-determining mid-stream byte;
    // 0xF6 is an opcode group keyed on a non-first-byte sub-field. (The 6502 declares NO
    // DecodeStructure — the default single-byte/fixed-length/key==opcode walk.)
    public static readonly DecodeStructure Decode = new(
        Prefixes: [new PrefixByte(0xCB)],                 // property 1+2: a prefix → multi-byte opcode
        ModRmOpcodes: [0x80],                             // property 1: 0x80 has a length-determining byte
        SubFieldOpcodes: [0xF6]);                         // property 3: 0xF6 keys on a non-first-byte sub-field

    public static readonly InstructionDef[] Instructions =
    [
        // (A) DEGENERATE — a plain single-byte op (the 6502 shape; key == opcode, length == 1).
        Insn(0xEA, "NOP", AddrMode.Implied, []),

        // (B) PROPERTY 1+2 — a PREFIXED, MULTI-BYTE opcode. Decode reads 0xCB (prefix), then 0x10
        //     (opcode); key == (0xCB << 8) | 0x10; length == 2.
        Insn(0xCB, 0x10, "PFXOP", AddrMode.Implied, [Load("A")]),

        // (C) the UNPREFIXED 0x10 — same opcode byte, DIFFERENT operation (proves the key
        //     distinguishes prefixed from unprefixed — the 256-table-cannot-express case, 0001-…:119).
        Insn(0x10, "BARE", AddrMode.Implied, []),

        // (D) PROPERTY 1 (mid-stream length byte) — 0x80 reads ONE more byte (a ModR/M-like byte)
        //     whose low 2 bits == disp-count (0/1/2). length is COMPUTED: 1 (opcode) + 1 (modrm) +
        //     dispCount. NOT a static field.
        Insn(0x80, "MODRMOP", AddrMode.Implied, [Load("A")]),

        // (E) PROPERTY 3 — 0xF6 is an opcode GROUP: the operation is selected by a SUB-FIELD (bits
        //     5-3) of the NEXT byte. key == (0xF6 << 3) | ((next >> 3) & 7). Two rows share opcode
        //     0xF6 but differ by the sub-field — the key resolves them to distinct operations.
        Insn(0xF6, subfield: 0, "GRP0", AddrMode.Implied, [Load("A")]),
        Insn(0xF6, subfield: 2, "GRP2", AddrMode.Implied, []),
    ];
}
```

*(The exact DSL spelling — `Insn(prefix, opcode, …)`, `Insn(opcode, subfield: n, …)`, the
`DecodeStructure` record — is defined in Ground truth G + Task 2; the above is the shape, Task 2's
literal code is authoritative.)*

### F.2 The three-property mapping (the explicit table the brief requires)

This maps each **8086 brief §10.1 decoder property** to the synthetic row that exercises it and the
generated-walk behavior the test asserts:

| 8086 brief §10.1 property | Brief citation | Synthetic row | What `Decode` does | Test assertion |
|---|---|---|---|---|
| **Property 1 — length is a COMPUTED OUTPUT of the walk (not a static `Length` field)** | `8086-…:789-796` | (D) `MODRMOP` (0x80) | reads 0x80 → `LengthRule.ModRmDetermined` → reads next byte → `Length = 2 + (thatByte & 3)` | `Decode([0x80,0x02,…])` → `Length == 4`; `Decode([0x80,0x00])` → `Length == 2`. **Same opcode, different length, by the mid-stream byte.** |
| **Property 1+2 — multi-byte opcode via a prefix (consume prefix → opcode)** | `8086-…:798-806`; `0001-…:117-119` | (B) `PFXOP` + (C) `BARE` | reads 0xCB → prefix → reads 0x10 → `key=(0xCB<<8)\|0x10`, `Length==2`; bare `[0x10]` → `key=0x10`, `Length==1` | `Decode([0xCB,0x10])` → key `0xCB10`, len 2, resolves `PFXOP`; `Decode([0x10])` → key `0x10`, len 1, resolves `BARE`. **Prefixed ≠ unprefixed for the same second byte.** |
| **Property 3 — the key includes a sub-field of a NON-FIRST byte (`opcode<<3 \| reg`)** | `8086-…:807-810, 740` | (E) `GRP0` + `GRP2` (0xF6) | reads 0xF6 → group → reads next → `key=(0xF6<<3)\|((next>>3)&7)`, `Length==2` | `Decode([0xF6,0b00_000_xxx])` → resolves `GRP0`; `Decode([0xF6,0b00_010_xxx])` → resolves `GRP2`. **Same opcode byte, different operation, by bits 5-3 of the second byte.** |
| **Degenerate — the 6502 case (key == opcode, fixed length)** | `0001-…:158-159` | (A) `NOP` (0xEA) | reads 0xEA → `Fixed`, `FixedLength==1` → `key=0xEA`, `Length==1` | `Decode([0xEA])` → key `0xEA`, len 1, resolves `NOP`. **The trivial walk the 6502 always takes.** |
| **Fetch-unit parameterization (byte vs word)** | `68000-…:747-750` | a `UnitBytes==2` micro-fixture | the walk reads via `NextUnit`; a word stream's `UnitBytes==2` makes `Length == 2 × units` | a `BufferFetchStream(UnitBytes: 2)` over a 1-unit op returns `Length == 2`. **The walk does not assume bytes.** |

### F.3 The JIT-reachable proof (the synthetic CPU through `Discover`)

Beyond the generator-text assertions, one test exercises `BlockCompiler.Discover` (or its decoder-walk
core) over the synthetic CPU's length-determining opcode and asserts **discovery advances PC by the
COMPUTED length** — the JIT-side half of property 1:

- A run starting at `MODRMOP` (0x80) with a mid-stream byte `0x02` (disp-count 2) advances the
  discovery cursor by 4, not by a static field; mid-stream `0x00` advances by 2. This proves
  `Discover` (Ground truth B) reads `r.Length` from the walk, not `d.Length` from a field — the J3
  generalization the 8086 brief calls the JIT-ism it "stresses most" (`8086-…:686`).

> **What the synthetic CPU does NOT do (scope honesty).** It does NOT compute a real EA from the
> ModR/M-like byte (no `seg<<4`, no base+index). The mid-stream byte affects ONLY `Length` (the
> property under test) and, for the sub-field case, the `key`. The synthetic ops are trivial
> (`NOP`/`Load("A")`) — the proof is the DECODE SHAPE (key + length), not the operands' semantics. This
> is precisely the brief's line: prove the decoder generalizes to Z80 prefixes AND 8086 ModR/M *before
> either CPU exists*, without implementing either CPU's operand semantics.

---

## Ground truth G — the decode-structure DSL surface (minimal, default-off)

**The contract:** a spec optionally declares a `DecodeStructure`; absent it, the CPU is a single-byte,
fixed-length, key-==-opcode decoder (the 6502 — UNCHANGED, no DSL edit). The `DecodeStructure` declares
the three non-degenerate properties:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>A spec's decode structure. ABSENT (the 6502 default) ⇒ single-byte opcode, key ==
/// opcode, length fixed per addressing mode — the degenerate walk. Declaring it opts into the
/// multi-byte / mid-stream-length / sub-field-key properties (Z80 prefixes, 8086 ModR/M). M3.1b
/// ships the SHAPE + the synthetic proof; no shipped CPU declares one yet (the 6502 doesn't).</summary>
public sealed record DecodeStructure(
    PrefixByte[] Prefixes,        // bytes that switch "page" (Z80 CB/ED/DD/FD) — property 1+2
    byte[] ModRmOpcodes,          // opcodes carrying a length-determining mid-stream byte — property 1
    byte[] SubFieldOpcodes);      // opcodes whose operation is refined by a non-first-byte sub-field — property 3

public sealed record PrefixByte(byte Value);
```

And the `Spec.Insn` factory gains overloads to key a row by `(prefix, opcode)` or `(opcode, subfield)`:

```csharp
// EXISTING (6502 — unchanged): single-byte opcode.
//   Insn(byte opcode, string mnemonic, AddrMode mode, Op[] ops)
// NEW overloads (the existing single-byte form is untouched):
//   Insn(byte prefix, byte opcode, string mnemonic, AddrMode mode, Op[] ops)   — a prefixed row
//   Insn(byte opcode, int subfield, string mnemonic, AddrMode mode, Op[] ops)  — an opcode-group row
// These set InstructionDef.OperationKey (a uint) per the KeyShape; the single-byte form sets
// OperationKey = opcode (the degenerate KeyShape.OpcodeByte). The model carries OperationKey
// (a uint) alongside the existing byte Opcode — but the 6502's OperationKey == its opcode, so
// Mos6502Spec.cs is byte-identical (it only ever uses the single-byte Insn).
```

> **The 6502 DSL is UNCHANGED.** Every 6502 `Insn(0xA9, "LDA", AddrMode.Immediate, [...])` uses the
> existing single-byte overload, which sets `OperationKey = 0xA9`, `KeyShape.OpcodeByte`.
> `Mos6502Spec.cs` does not change a byte — the `RegeneratedSpecTests` anchor holds unchanged. Only the
> synthetic spec uses the new overloads. This is the "the 6502 spec and its generated output do not
> change" property ADR Decision 1 promised (`0001-…:158-159`: "the 6502 declares no prefixes and the
> existing single-table path is the zero-prefix special case").

---

## Authorized test changes (the only existing tests that move)

This is a PURE REFACTOR: the existing suite must stay green with **only** the changes enumerated here.
Any other test that turns red is a refactor bug, not an authorized change. A Task 0 grep
(`d\.Length`, `OpcodeDescriptor(`, `JitDescriptors`, `InstructionLength`, `Discover`, `new OpcodeDescriptor`,
`Length:`) produces the exact hit list; the table below is the predicted set — a hit not in this list
is a STOP.

| # | Test (file) | Change | Why authorized |
|---|---|---|---|
| 1 | `OpcodeDescriptorTests` (`tests/.../Jit/`) — any test constructing an `OpcodeDescriptor` with a positional `Length` | the ctor arg `Length: N` → `LengthRule.Fixed, FixedLength: N` | the descriptor record shape changed (Ground truth B); these tests construct it directly |
| 2 | `OpcodeDescriptorTests` — `Undefined` sentinel test | the sentinel now sets `LengthRule.Fixed, FixedLength: 1` (was `Length: 1`) | `OpcodeDescriptor.Undefined` (`OpcodeDescriptor.cs:55-58`) is updated; its test asserts the new fields |
| 3 | The generator snapshot test (`tests/.../Generators/…snapshot…`) | re-snap: the `JitDescriptors` rows + the NEW `Decode`/`DescriptorFor` methods + the `Step`/`InstructionLength`/`Disassemble` body changes | the generated `.g.cs` text changed in the enumerated regions (Ground truth E); the re-snap is the mechanical authorized capture |
| 4 | Any JIT `Discover`/block test asserting a PC-advance via a descriptor `Length` field | read the computed length from the run/result, not `d.Length` | `Discover` returns `(pc, d, length)` tuples now (Ground truth B); tests reading `.Length` off the descriptor move to the tuple's length |
| 5 | Any monitor/`InstructionLength` test that mocks/stubs the old `switch(opcode)` length path | call through the generated `InstructionLength` (signature UNCHANGED) | the *signature* is stable (Ground truth E note); only tests that reached *inside* the old switch move — most monitor tests are black-box and DO NOT change |
| 6 | `Importer/OpcodeDatasetTests` — if any test asserts `ExpectedBytes`/the single-byte `OpcodeFormat` is the ONLY accepted form | relax to "the 6502 rows still validate; a computed-length row is not forbidden" (Task 6) | the importer's 6502 rules are unchanged for 6502 data; Task 6 only stops the schema from *forbidding* a future computed length — see Task 6 scope |

**Tests that DO NOT change (and why that is the proof):**
- **Every TomHarte test** (both tiers, 1.51M cases) — behavior-identical; any change is a bug.
- **Every Klaus test** (both tiers, 96,241,367 cycles) — cycle-identical.
- **Every interpreter `Op{XX}` body test / disassembler golden test** — the bodies + disasm text are
  byte-identical for the 6502 (key == opcode; bodies advance PC unchanged).
- **`RegeneratedSpecTests`** — `Mos6502Spec.cs` is byte-identical (the 6502 DSL is unchanged).
- **`SyntheticRegisterSetTests`** (M3.1a's `BC`/`HL` fixture) — untouched; this plan adds a NEW
  `SyntheticDecodeStructureTests` fixture beside it, it does not modify the register one.

**Authorized test-change count: 6 categories** (rows 1-6 above); rows 3 (re-snap) and 5 (black-box
monitor) are expected to touch the fewest individual tests. The bulk of the suite is the
DO-NOT-CHANGE set — that invariance is the pure-refactor proof.

---

## File structure

```
src/CpuEmulator.Core/Jit/
    OpcodeDescriptor.cs       — MODIFY (int Length -> LengthRule + FixedLength; add LengthRule enum)
    DecodeResult.cs           — NEW (DecodeResult, DecodedOperands, KeyShape)
    IDecoder.cs               — NEW (IDecoder, IFetchStream)
    BufferFetchStream.cs      — NEW (the byte[]/span-backed stream; UnitBytes ctor arg)
src/CpuEmulator.Core/Specification/
    DecodeStructure.cs        — NEW (DecodeStructure, PrefixByte; default-off)
    InstructionDef.cs         — MODIFY (add OperationKey + the (prefix,opcode)/(opcode,subfield) carriers)
    Spec.cs                   — MODIFY (Insn overloads for prefixed + opcode-group rows; single-byte UNCHANGED)
src/CpuEmulator.Generators/
    SpecModel.cs              — MODIFY (InstructionModel gains OperationKey + KeyShape; SpecModel gains
                                optional DecodeStructure + FetchUnit, defaulting to byte/none)
    SpecParser.cs             — MODIFY (parse the optional DecodeStructure; parse the new Insn overloads;
                                opcode-range check stays for single-byte rows; CPUGEN for malformed decode struct)
    CpuEmitter.cs             — MODIFY (emit Decode + DescriptorFor; route Step/Execute/InstructionLength/
                                Disassemble through them; JitDescriptors rows emit LengthRule+FixedLength)
src/CpuEmulator.Jit/
    BlockCompiler.cs          — MODIFY (Discover runs the walk; advances by computed length; run tuple gains
                                Length; PagesSpanned reads it; BusFetchStream wiring; J1 typeof(Mos6502Cpu) STAYS)
    BusFetchStream.cs         — NEW (the IAddressSpace-backed byte stream with SeekTo)
tools/CpuEmulator.SpecImporter/
    OpcodeDataset.cs          — MODIFY (Task 6: do not FORBID a computed-length row; 6502 rules unchanged)
src/CpuEmulator.Cpus.Mos6502/
    Mos6502Spec.cs            — UNCHANGED (the 6502 DSL is single-byte; byte-identical)
tests/CpuEmulator.Tests/
    Jit/DecodeWalkTests.cs              — NEW (Task 1: DecodeResult + the byte-stream walk contract)
    Jit/OpcodeDescriptorTests.cs        — MODIFY (Length -> LengthRule/FixedLength; authorized rows 1-2)
    Jit/DiscoverComputedLengthTests.cs  — NEW (Task 4: Discover advances by the COMPUTED length)
    Generators/SyntheticDecodeStructureTests.cs — NEW (Task 7: the three-property synthetic proof)
    Generators/<snapshot>.cs            — MODIFY (Task 5: re-snap the generated decode regions)
    Importer/OpcodeDatasetTests.cs      — MODIFY (Task 6: computed-length not forbidden; 6502 unchanged)
    (any other test embedding `d.Length`/`OpcodeDescriptor(`/`JitDescriptors` — enumerated by Task 0 grep)
```

---

## Task 0: Baseline + the `d.Length` / `OpcodeDescriptor(` / `JitDescriptors` blast-radius grep (NO code change)

> Establish the exact green baseline and enumerate every site that reads a static `Length`, constructs
> an `OpcodeDescriptor`, indexes `JitDescriptors`, or `switch`es on a raw opcode, BEFORE touching code,
> so the refactor is a known, bounded edit set — not a whack-a-mole. Mirrors M3.1a Task 0.

- [ ] **Step 1: Branch check** — `git branch --show-current` → `feat/m3-generic-decoder` (base `main`,
  head `3d1eff4` — the M3.1a merge). This plan file is the preparatory doc commit on it.
- [ ] **Step 2: Confirm the green baseline** — `dotnet test` (routine suite, excl. the heavy Klaus/
  full-TomHarte sweeps). **Record the EXACT test count** (the brief says ~1436; pin the real number —
  the per-task estimates are relative to it). Confirm 0 failures, 0 unexpected skips. Record
  `dotnet build --no-incremental -warnaserror` is clean.
- [ ] **Step 3: The blast-radius grep** (record the hit list in the closeout; this is the authorized
  edit set — anything not foreseen by the File-structure list is a STOP to add before proceeding):
  - `d\.Length` across `src/`, `tools/`, `tests/` — every reader of the static descriptor length
    (expected: `BlockCompiler.cs:104` (`pc += d.Length`), `:129, 136` (the successor-PC math),
    `:148` (`PagesSpanned`'s `b < d.Length`)).
  - `\.Length\b` on an `OpcodeDescriptor`/`d` receiver (the field reads that move to the computed
    run/result length).
  - `OpcodeDescriptor(` / `new OpcodeDescriptor` / `OpcodeDescriptor.Undefined` — the ctor + sentinel
    sites (the record shape changes: `int Length` → `LengthRule, int FixedLength`).
  - `JitDescriptors\[` — the dense `[256]` index sites (`BlockCompiler.cs:101`, the generated
    `Mos6502Cpu.g.cs`); they route through `DescriptorFor(key)` after the refactor.
  - `InstructionLength` / `ModeLength` — the monitor length path (`CpuEmitter.cs:999, 1014`).
  - `Length:` in test sources (the positional/named `Length` ctor args that migrate to
    `LengthRule.Fixed, FixedLength:` — authorized rows 1-2).
  - `switch (opcode)` / `Execute(byte opcode)` — the four decode sites the walk collapses
    (`CpuEmitter.cs:127, 1016, 1253`; the disasm + InstructionLength + Execute switches).
  Confirm the hit set matches the File-structure list + the decode-site inventory table; a hit not in
  either is a STOP — add it to the plan (with a note) before proceeding.
- [ ] **Step 4:** No commit (read-only task). Proceed to Task 1.

---

## Task 1: `DecodeResult` / `IFetchStream` / `IDecoder` + the byte-stream walk contract (TDD)

> Maps to scope item 1 + Ground truth A. The foundation: the CPU-agnostic value types and the
> fetch-stream abstraction the walk reads through. NO generated code yet — these are hand-written
> `Core` types + the test-facing `BufferFetchStream`, pinned by a direct unit test of the walk
> contract (a stub decoder over a `BufferFetchStream` returns `Length == UnitsConsumed * UnitBytes`).

**Files:** NEW `src/CpuEmulator.Core/Jit/DecodeResult.cs` (`DecodeResult`, `DecodedOperands`,
`KeyShape`); NEW `src/CpuEmulator.Core/Jit/IDecoder.cs` (`IDecoder`, `IFetchStream`); NEW
`src/CpuEmulator.Core/Jit/BufferFetchStream.cs`; NEW `tests/CpuEmulator.Tests/Jit/DecodeWalkTests.cs`.

- [ ] **Step 1: Failing tests** (`DecodeWalkTests`):
  - `BufferFetchStream_NextUnit_advances_and_returns_the_byte` — a `BufferFetchStream([0xEA, 0x12])`
    returns `0xEA` then `0x12` from `NextUnit()`, and `UnitsConsumed == 2`, `UnitBytes == 1` after.
  - `BufferFetchStream_PeekUnit_does_not_advance` — `PeekUnit()` returns `0xEA` twice without
    advancing; a following `NextUnit()` still returns `0xEA`.
  - `BufferFetchStream_word_unit_reads_two_bytes_per_unit` — a `BufferFetchStream([0x34, 0x12],
    unitBytes: 2)` returns `0x1234` (little-endian) from one `NextUnit()`, with `UnitsConsumed == 1`
    and `UnitBytes == 2` (Ground truth D's word-unit micro-proof — the walk does not assume bytes).
  - `Length_equals_units_consumed_times_unit_bytes` — a tiny inline stub `IDecoder` that consumes 2
    units then sets `Length = stream.UnitsConsumed * stream.UnitBytes` returns `Length == 2` over a
    byte stream and `Length == 4` over a `unitBytes: 2` stream. **The load-bearing contract: length is
    a COMPUTED output of consumption, never a field read.**
  - `DecodedOperands_None_is_zero` — `DecodedOperands.None == new(0, 0, 0)`.

- [ ] **Step 2: Author the `Core` value types** (`DecodeResult.cs` — the Ground truth A.1 literal,
  verbatim): the `DecodeResult(uint OperationKey, int Length, DecodedOperands Operands)` record struct,
  the `DecodedOperands(byte Lo, byte Hi, byte Count)` record struct with the `None` static, and the
  `KeyShape { OpcodeByte, PrefixedOpcode, OpcodeGroup }` enum (Ground truth C). Author the
  `IFetchStream` / `IDecoder` interfaces (the Ground truth A.2 literal, verbatim — `UnitBytes`,
  `NextUnit`, `PeekUnit`, `UnitsConsumed`; `IDecoder.Decode(IFetchStream)`).

- [ ] **Step 3: Author `BufferFetchStream`** (the byte[]/span-backed test/monitor stream — Ground
  truth A.3 #2). `UnitBytes` is a ctor arg (default 1); `NextUnit` reads `UnitBytes` bytes
  little-endian from the cursor and advances by one unit; `PeekUnit` reads without advancing;
  `UnitsConsumed` counts units:

```csharp
namespace CpuEmulator.Core.Jit;

/// <summary>An IFetchStream over an in-memory byte buffer — the test/monitor stream (the
/// disassembler + InstructionLength are handed an instruction's bytes, not a live bus). UnitBytes
/// defaults to 1 (byte-granular: 6502/Z80/8086); a ctor arg sets it to 2 so the word-unit
/// micro-proof (Ground truth D / F.2) exercises a 68000-shaped fetch without a 68000. NextUnit
/// reads UnitBytes bytes little-endian and advances one unit; Length = UnitsConsumed × UnitBytes.</summary>
public sealed class BufferFetchStream : IFetchStream
{
    private readonly System.ReadOnlyMemory<byte> _buffer;
    private int _byteCursor;

    public BufferFetchStream(System.ReadOnlyMemory<byte> buffer, int unitBytes = 1)
    {
        if (unitBytes is not (1 or 2))
            throw new System.ArgumentOutOfRangeException(nameof(unitBytes), "fetch unit must be 1 or 2 bytes");
        _buffer = buffer;
        UnitBytes = unitBytes;
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
        for (int i = 0; i < UnitBytes; i++)            // little-endian: byte 0 is the low byte
            v |= (uint)b[_byteCursor + i] << (8 * i);
        return v;
    }
}
```

- [ ] **Step 4: Tests pass; full suite green** (these are additive `Core` types — nothing downstream
  consumes them yet, so the existing suite is untouched). **Commit** —

  ```
  feat(core): DecodeResult + IFetchStream/IDecoder + BufferFetchStream — the decode-walk contract

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

---

## Task 2: `OpcodeDescriptor.Length` → `LengthRule` + `FixedLength` (TDD)

> Maps to scope item 2 + Ground truth B. The static `int Length` field is removed; the descriptor
> carries the data the walk needs to COMPUTE length (a `LengthRule` + the per-mode `FixedLength`
> constant). This is a `Core` type change with NO behavior — every reader still gets the same value,
> now via the rule. Lands the authorized test rows 1-2 (`OpcodeDescriptorTests`).

**Files:** `src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs` (add `LengthRule` enum; `int Length` →
`LengthRule LengthRule, int FixedLength`; update `Undefined`); `tests/.../Jit/OpcodeDescriptorTests.cs`
(authorized rows 1-2). NOTE: this task changes the record SHAPE — `BlockCompiler` (Task 3's combined
edit) and the generated `JitDescriptors` rows (Task 3) must move in the SAME landing or the build
breaks. **Recommended sequencing: this `Core` type change is the first commit of a combined Task 2+3
landing** (the type change has no behavior; the consumers follow immediately) — see Task 3's note.

- [ ] **Step 1: Failing/changed tests** (`OpcodeDescriptorTests`):
  - `Descriptor_carries_a_LengthRule_and_FixedLength` (authorized row 1) — an `OpcodeDescriptor`
    constructed with `LengthRule.Fixed, FixedLength: 3` exposes `LengthRule == LengthRule.Fixed` and
    `FixedLength == 3` (the ctor-arg migration from positional `Length: 3`).
  - `Undefined_sentinel_is_Fixed_length_1` (authorized row 2) — `OpcodeDescriptor.Undefined(0x02)`
    has `LengthRule == LengthRule.Fixed`, `FixedLength == 1` (was `Length: 1`), `NeedsFallback` +
    `EndsBlock` true (unchanged).
  - `ModRmDetermined_rule_is_expressible` — a descriptor constructed with `LengthRule.ModRmDetermined,
    FixedLength: 2` (the base before the variable tail) round-trips both fields. (Pins the enum's
    second member exists for the synthetic CPU; no 6502 row uses it.)

- [ ] **Step 2: Add the `LengthRule` enum + reshape the record** (`OpcodeDescriptor.cs` — the Ground
  truth B literal, verbatim). The `int Length` positional arg is REPLACED by `LengthRule LengthRule,
  int FixedLength`; `BaseCycles`/`PageCrossPenalty`/`NeedsFallback`/`EndsBlock`/`Ops` are UNCHANGED
  (cycle model out of scope — recorded deviation). Update the `Undefined` factory:

```csharp
    public static OpcodeDescriptor Undefined(byte opcode) => new(
        opcode, "???", JitMode.Implied, JitOpClass.Undefined,
        LengthRule.Fixed, FixedLength: 1, BaseCycles: 0, PageCrossPenalty: false,
        NeedsFallback: true, EndsBlock: true, Ops: []);
```

  Update the field doc comment on `LengthRule`/`FixedLength` (Ground truth B's rationale: `FixedLength`
  is the walk's INPUT for the easy case — the walk consumes `FixedLength` units and returns
  `UnitsConsumed`, still a computation, not a bypass).

- [ ] **Step 3: Tests pass.** The full suite at this point does NOT build yet (every `JitDescriptors`
  row + `BlockCompiler`'s `d.Length` reference the old shape) — this is the half-migrated window the
  combined Task 2+3 landing closes. **Do NOT commit standalone**; carry into Task 3's landing (or, if
  committing the `Core` type alone for review granularity, mark the commit as a known-not-building
  intermediate and immediately proceed to Task 3 in the same session). **Recommended commit (combined
  with Task 3)** — see Task 3 Step 6.

---

## Task 3: The emitter `Decode` + `DescriptorFor` walk; route Step/Discover/disasm/asm/InstructionLength through it (TDD)

> Maps to scope items 1+5 + Ground truths C/E. The four `switch(opcode)` decode sites collapse to ONE
> generated walk. The generator emits a `Decode(IFetchStream)` + a `DescriptorFor(uint)` resolver; the
> interpreter `Step`, JIT `Discover`, the disassembler, and `InstructionLength` all route through them.
> For the 6502 (every opcode `LengthRule.Fixed`, `key == opcode`) the BEHAVIOR is byte-identical; the
> generated `.g.cs` TEXT changes in the enumerated regions (Ground truth E). Lands WITH Task 2's
> `Core` type change (shared descriptor shape) — the recommended single landing.

**Files:** `src/CpuEmulator.Generators/SpecModel.cs` (InstructionModel gains `uint OperationKey` +
`KeyShape`; SpecModel gains optional `DecodeStructure` + `FetchUnit`, defaulting to byte/none — wired
fully in Task 7; here defaulted so the 6502 path is unchanged); `src/CpuEmulator.Generators/CpuEmitter.cs`
(emit `Decode` + `DescriptorFor`; `JitDescriptors` rows emit `LengthRule.Fixed, FixedLength: N`; route
`Step`/`Execute`/`InstructionLength`/`Disassemble` through the walk); `src/CpuEmulator.Jit/BlockCompiler.cs`
(`Discover` runs the walk, advances by computed length; run tuple gains `Length`; `PagesSpanned` reads
it); NEW `src/CpuEmulator.Jit/BusFetchStream.cs`.

- [ ] **Step 1: Failing/spot tests** (split across the JIT + generator test files; the byte-identical
  behavior is proven by the UNCHANGED existing suite — these are the SHAPE pins):
  - `Generated_Decode_for_6502_NOP_returns_key_opcode_length_1` — generate the 6502 (or a subset
    fixture), drive the generated `Decode(new BufferFetchStream([0xEA]))` → `OperationKey == 0xEA`,
    `Length == 1` (the degenerate walk — Ground truth F row A).
  - `Generated_Decode_for_LDA_immediate_returns_length_2` — `Decode([0xA9, 0x42])` → `OperationKey ==
    0xA9`, `Length == 2`, `Operands.Lo == 0x42`, `Operands.Count == 1` (the operand byte the mode
    consumes; byte-identical to the old `ModeLength`).
  - `DescriptorFor_6502_key_is_the_dense_array_index` — `Mos6502Cpu.DescriptorFor(0xA9)` returns the
    same descriptor as `JitDescriptors[0xA9]` (Ground truth C: for the 6502 `DescriptorFor(key) ==
    JitDescriptors[(byte)key]` — zero hot-path regression).
  - `JitDescriptors_row_emits_LengthRule_Fixed_and_FixedLength` (generator snapshot spot) — the
    emitted `LDA` row text contains `LengthRule.Fixed, FixedLength: 2` (not a positional `2` in the
    old `Length` slot) — Ground truth E.
  - `InstructionLength_6502_routes_through_the_walk` — `Mos6502Cpu.InstructionLength(0xA9) == 2`,
    `InstructionLength(0xEA) == 1` (signature UNCHANGED — Ground truth E note; body routes through the
    walk / `DescriptorFor(opcode).FixedLength`).

- [ ] **Step 2: `BusFetchStream`** (the `IAddressSpace`-backed byte stream with `SeekTo` — Ground
  truth A.3 #1). `UnitBytes == 1`; `NextUnit` == `Read8(pc); pc++` (NO cycle charge — `Discover` is a
  debugger-view decode that never executes, exactly as the current `Discover` reads `_bus.Read8(pc)`
  WITHOUT charging, `BlockCompiler.cs:100`); `PeekUnit` reads without advancing; `SeekTo(pc)`
  repositions between instructions:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Core.Jit;

namespace CpuEmulator.Jit;

/// <summary>An IFetchStream over a live IAddressSpace at a PC — the byte-granular default the JIT
/// Discover (and, wrapped, the interpreter Step) read through. UnitBytes == 1; NextUnit reads the
/// byte at the cursor and advances (a debugger-view decode — it does NOT charge a cycle; Discover
/// never executes, matching today's bus.Read8(pc) at BlockCompiler.cs:100). SeekTo repositions the
/// cursor so Discover can walk instruction to instruction by the COMPUTED length.</summary>
internal sealed class BusFetchStream : IFetchStream
{
    private readonly IAddressSpace _bus;
    private ushort _origin;
    private int _offset;

    public BusFetchStream(IAddressSpace bus, ushort pc) { _bus = bus; _origin = pc; }

    public int UnitBytes => 1;
    public int UnitsConsumed => _offset;

    public uint NextUnit()
    {
        byte b = _bus.Read8((ushort)(_origin + _offset));
        _offset++;
        return b;
    }

    public uint PeekUnit() => _bus.Read8((ushort)(_origin + _offset));

    /// <summary>Reposition at a new PC and reset the consumed count (between instructions).</summary>
    public void SeekTo(ushort pc) { _origin = pc; _offset = 0; }
}
```

- [ ] **Step 3: Emit the `Decode` walk + `DescriptorFor`** (`CpuEmitter.cs`). For the 6502 (no
  `DecodeStructure` — the default), `Decode` is the degenerate byte-key/fixed-length walk and
  `DescriptorFor` is the dense `[256]` index. The generated bodies (shape — the emitter writes these
  per spec; for the 6502 the `DecodeStructure` is absent so this default emits):

```csharp
    /// <summary>The generated decode walk (Ground truth A): consume the opcode unit, look up its
    /// descriptor, consume the operand units its mode/length-rule implies, and return
    /// (OperationKey, COMPUTED Length, Operands). For the 6502 every row is LengthRule.Fixed and the
    /// key is the opcode byte — the degenerate walk. Length == stream.UnitsConsumed × stream.UnitBytes.</summary>
    public static CpuEmulator.Core.Jit.DecodeResult Decode(CpuEmulator.Core.Jit.IFetchStream stream)
    {
        uint opcode = stream.NextUnit();                       // consume the opcode unit
        uint key = opcode;                                     // 6502: KeyShape.OpcodeByte — key == opcode
        CpuEmulator.Core.Jit.OpcodeDescriptor d = DescriptorFor(key);
        byte lo = 0, hi = 0, count = 0;
        // Fixed rule: consume (FixedLength - 1) operand units (the mode's operand bytes).
        for (int i = 1; i < d.FixedLength; i++)
        {
            byte b = (byte)stream.NextUnit();
            if (count == 0) { lo = b; count = 1; }
            else { hi = b; count = 2; }
        }
        int length = stream.UnitsConsumed * stream.UnitBytes;  // COMPUTED — not a field read
        return new CpuEmulator.Core.Jit.DecodeResult(key, length, new(lo, hi, count));
    }

    /// <summary>Resolve an operation-key to its descriptor (Ground truth C). 6502: the dense [256]
    /// array index — key fits a byte, so zero hot-path regression. A prefixed CPU would emit a
    /// Dictionary&lt;uint, OpcodeDescriptor&gt; here; the consumers call this, never the raw table,
    /// so the key packing is fully encapsulated.</summary>
    public static CpuEmulator.Core.Jit.OpcodeDescriptor DescriptorFor(uint operationKey) =>
        JitDescriptors[(byte)operationKey];
```

  > **Why the `Decode` loop carries `lo`/`hi` separately rather than over the disassembler's
  > parameters.** The disassembler keeps its `(byte opcode, byte operandLo, byte operandHi)` signature
  > (Ground truth E note — `IMonitorSupport` stable). `Decode`'s `DecodedOperands(lo, hi, count)`
  > carries exactly the bytes a 6502 mode reads after the opcode; the monitor routes through a
  > `BufferFetchStream([opcode, operandLo, operandHi])` to get the key + length, then formats by key.
  > For the 6502, `count` is `FixedLength - 1` (0/1/2), byte-identical to `ModeLength(mode) - 1`.

- [ ] **Step 4: `JitDescriptors` rows emit `LengthRule.Fixed, FixedLength: N`** — change
  `DescriptorLiteral` (`CpuEmitter.cs:1329-1344`): the `{length}` positional arg becomes
  `CpuEmulator.Core.Jit.LengthRule.Fixed, {length}` (the rule + the per-mode constant). `ModeLength`
  still computes the constant — it is now `FixedLength`, the walk's input for the easy case (Ground
  truth B). The `Undefined` sentinel rows already carry the new shape (Task 2).

- [ ] **Step 5: Route the four decode sites through the walk** (`CpuEmitter.cs` + `BlockCompiler.cs`):
  - **Interpreter `Step`** (`CpuEmitter.cs:98-105`): the `byte opcode = ReadBus(PC); PC++;
    Execute(opcode)` shape stays for the 6502 (the per-op bodies still do their own `ReadBus(PC); PC++`
    — Ground truth E: bodies UNCHANGED, byte-identical). `Execute` now dispatches on the resolved
    operation-key; for the 6502 `key == opcode`, so the generated `switch` arms are textually
    identical (Ground truth E: the `Execute` switch is "now over the key not a raw fetched byte — for
    the 6502 they are equal"). The interpreter does NOT re-walk per instruction (its per-mode bodies
    are the authoritative per-template PC advance — the four-notions-of-length note, #1 stays
    per-body); `Step`'s contract with the walk is that the walk's `FixedLength` MIRRORS what the body
    consumes (pinned by the Task 5 cross-check). Recorded: the interpreter keeps its body-driven PC
    advance; the walk is the JIT/monitor's shared length computation.
  - **JIT `Discover`** (`BlockCompiler.cs:95-107`): rewrite to the Ground truth B literal — run the
    walk via a `BusFetchStream`, advance by `r.Length`, `SeekTo` the next PC. The run tuple grows a
    `Length` member; `Compile`'s loop + successor-PC math (`:124-136`) read the tuple's `Length`;
    `PagesSpanned` (`:143-151`) reads the tuple `Length` not `d.Length`:

```csharp
    public System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length)> Discover(ushort pc)
    {
        var run = new System.Collections.Generic.List<(ushort, OpcodeDescriptor, int)>();
        var stream = new BusFetchStream(_bus, pc);              // byte-granular, positioned at pc
        for (int i = 0; i < _opts.BlockLengthCap; i++)
        {
            DecodeResult r = Mos6502Cpu.Decode(stream);         // the walk — computes key + length
            OpcodeDescriptor d = Mos6502Cpu.DescriptorFor(r.OperationKey);  // 6502: key == opcode → [256]
            run.Add((pc, d, r.Length));
            if (d.EndsBlock) break;
            pc = unchecked((ushort)(pc + r.Length));            // advance by the COMPUTED length
            stream.SeekTo(pc);                                  // reposition at the next instruction
        }
        return run;
    }
```

    Update `Compile` (`:124-136`) and `EmitInstruction`/`EmitBudgetCheck`/`EmitChainOrExit` callers to
    read the tuple's stored `Length` (the computed value) wherever they read `d.Length` today; update
    `PagesSpanned` to `for (int b = 0; b < length; b++)`. (J1 `typeof(Mos6502Cpu)` STAYS — `Decode`/
    `DescriptorFor` are called as static members of the concrete `Mos6502Cpu`; the CPU TYPE baked-ness
    is the J1 deferral, unchanged — recorded.)
  - **Disassembler** (`CpuEmitter.cs:1247-1294`): signature UNCHANGED (`Disassemble(byte opcode, byte
    operandLo, byte operandHi)`). The `switch` is now over the operation-key; for the 6502 `key ==
    opcode`, so the arms are textually identical (Ground truth E: "MINIMAL (body)"). The monitor passes
    the bytes it has; internally the key is `opcode` for the 6502.
  - **`InstructionLength`** (`CpuEmitter.cs:1011-1025`): signature UNCHANGED; body becomes
    `DescriptorFor(opcode).FixedLength` (every 6502 op is `LengthRule.Fixed`, so this is the value
    `ModeLength` produced — Ground truth E note). For a future `ModRmDetermined` opcode the body would
    route through `Decode(new BufferFetchStream(...))` to get `r.Length`; the 6502 takes the Fixed fast
    path.
  - **`TryAssemble`** (`CpuEmitter.cs:1027+`): UNCHANGED in direction (mnemonic→bytes, the decode
    inverse). It emits the bytes the walk would consume; for the 6502 this is identical to today.

- [ ] **Step 6: Regenerate the 6502; full suite green.** This task changes generated `.g.cs` text (the
  re-snap is Task 5; here the suite must still pass with the NEW behavior matching the OLD — byte-
  identical 6502). Run the JIT parity pins + the interpreter-body emission suite. **Commit (combined
  2+3 — the descriptor shape + its producer/consumers land together)** —

  ```
  refactor(decode): one generated Decode walk — length is a COMPUTED output, not a static field

  OpcodeDescriptor.Length (int) -> LengthRule + FixedLength; the generator emits a single
  Decode(IFetchStream)/DescriptorFor(uint) walk replacing the four switch(opcode) sites;
  Discover advances by the walk's computed length. 6502 behavior byte-identical (every row
  LengthRule.Fixed, key == opcode).

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

---

## Task 4: `Discover` advances by the COMPUTED length — the JIT-reachable proof (TDD)

> Maps to Ground truth B + F.3. Pins that `Discover` reads `r.Length` from the walk, NOT `d.Length`
> from a field — the J3 generalization the 8086 brief calls the JIT-ism it "stresses most". For the
> 6502 this is byte-identical (the walk returns the per-mode constant); the COMPUTED-length-varies
> proof is the synthetic CPU (Task 7 / F.3), but the 6502 half is pinned here over the real table.

**Files:** NEW `tests/CpuEmulator.Tests/Jit/DiscoverComputedLengthTests.cs`; authorized row 4 (any JIT
block test reading `d.Length` moves to the run tuple's `Length`).

- [ ] **Step 1: Failing tests** (`DiscoverComputedLengthTests` — drive a `BlockCompiler` over a small
  hand-laid program in an `AddressSpace`, via the existing JIT test harness pattern):
  - `Discover_advances_by_the_walk_length_over_mixed_modes` — a program `LDA #$01` (2 bytes) `NOP` (1)
    `LDA $1234` (3) `BRK` discovers four rows whose `(Pc)` are the running sum of the COMPUTED lengths
    (`entry, entry+2, entry+3, entry+6`), and each run tuple's `Length` equals `2,1,3,1`. **Asserts
    the cursor advanced by `r.Length`, the walk's output — not a static field read.**
  - `Discover_run_tuple_carries_the_computed_length` — the run is `List<(ushort, OpcodeDescriptor,
    int Length)>` and tuple `.Length` matches `DescriptorFor(key).FixedLength` for each 6502 row (the
    Fixed degenerate equality).
  - `PagesSpanned_uses_the_computed_length` — a block whose last instruction straddles a page boundary
    (`LDA $XXFF`-style) reports both pages in `PagesSpanned`, computed from the tuple `Length` not
    `d.Length` (authorized row 4 — the only other former `d.Length` reader).

- [ ] **Step 2: No new production code** — Task 3 already rewrote `Discover`/`PagesSpanned` to the
  computed-length form. This task is the PIN that the rewrite is correct over the real 6502 table
  (the synthetic varying-length proof is Task 7). If any existing JIT block test read `d.Length`, it
  moves to the tuple's `Length` here (authorized row 4).

- [ ] **Step 3: Full suite green** (incl. the JIT parity battery — the computed length must match the
  old field for every 6502 opcode, or it is a refactor bug). **Commit** —

  ```
  test(jit): Discover advances by the walk's computed length (J3); PagesSpanned reads it

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

---

## Task 5: Re-snap the generated decode regions + the interpreter-body cross-check (TDD)

> Maps to Ground truth E + the four-notions-of-length convergence note. The generated `Mos6502Cpu.g.cs`
> gained `Decode`/`DescriptorFor` and changed the `JitDescriptors` rows + the `InstructionLength`/
> `Disassemble`/`Execute` bodies. This task RE-SNAPS those regions (authorized row 3) and adds the
> cross-check pin that the walk's per-mode `FixedLength` equals what each interpreter body consumes
> (the convergence of length-notions (1) and (2)/(3) — the body PC advance and the walk agree).

**Files:** `tests/CpuEmulator.Tests/Generators/<snapshot>.cs` (re-snap — authorized row 3); the
cross-check pin (in `DecodeWalkTests` or a new `DecodeLengthCrossCheckTests`).

- [ ] **Step 1: The cross-check pin** (the load-bearing correctness gate, authored BEFORE re-snapping):
  - `Walk_FixedLength_matches_interpreter_body_PC_advance_for_every_opcode` — for every defined 6502
    opcode, assert `DescriptorFor(opcode).FixedLength == InstructionLength(opcode)` AND that both equal
    the old `ModeLength(mode)` value (a table the test computes from `JitMode`). This pins notions
    (2) `FixedLength`, (3) `InstructionLength`, and the per-mode constant agree — so the walk
    (JIT/monitor) and the per-body PC advance (interpreter, notion (1)) cannot drift. (Notion (4), the
    importer `ExpectedBytes`, is pinned in Task 6.)

- [ ] **Step 2: Re-snap the generated decode regions** (authorized row 3) — regenerate the 6502 and
  update the generator snapshot for: the NEW `Decode` + `DescriptorFor` methods; the `JitDescriptors`
  rows (`LengthRule.Fixed, FixedLength: N`); the `Execute`/`InstructionLength`/`Disassemble` body
  changes. **The re-snap is mechanical** — review the diff to CONFIRM it is exactly the enumerated
  Ground truth E regions and nothing else (state fields, `Op{XX}()` bodies, `GetRegister`/`SetRegister`,
  `TryAssemble` must be BYTE-IDENTICAL — any change there is a refactor bug, a STOP).

- [ ] **Step 3: Spot-pin the byte-identical regions** (Ground truth E "NO" rows):
  - `Interpreter_body_for_LDA_is_unchanged` — the `OpA9` body text is byte-identical to the
    pre-refactor output (the per-op bodies do not move — Ground truth E).
  - `TryAssemble_is_unchanged` — a `TryAssemble("LDA", "#$42")` spot pin returns the identical bytes
    (the decode inverse is unchanged in direction).

- [ ] **Step 4: Full suite green; the re-snapped snapshot passes.** **Commit** —

  ```
  test(generators): re-snap the generated decode regions; cross-check walk length vs body PC advance

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

---

## Task 6: Importer — a computed-length row is not FORBIDDEN; 6502 rules unchanged (TDD)

> Maps to scope item (the importer touch) + Ground truth (the four-notions note, notion (4)). The
> importer derives bytes from mode (`OpcodeDataset.ExpectedBytes`, `:146`) and ENFORCES
> `d.Bytes == ExpectedBytes(mode)` (`:135-137`). M3.1b touches this ONLY to stop the schema from
> FORBIDDING a future computed-length row — it does NOT add Z80/8086 data and does NOT change the
> 6502's rules (every 6502 row still validates byte-for-byte). This is the minimal seam, not the
> consumer.

**Files:** `tools/CpuEmulator.SpecImporter/OpcodeDataset.cs` (the byte-count check — relax to "6502
modes still validate; a computed-length row is not rejected"); `tests/.../Importer/OpcodeDatasetTests.cs`
(authorized row 6).

- [ ] **Step 1: Failing/changed tests** (`OpcodeDatasetTests`):
  - `Existing_6502_rows_still_validate_byte_for_byte` — the real `mos6502-opcodes.json` loads with
    `report.Emitted` unchanged and ZERO byte-count errors (the 6502 rules are UNCHANGED — the headline
    invariant; this is the regression guard, not a new behavior).
  - `A_computed_length_mode_is_not_forbidden` — a synthetic dataset row declaring a mode/marker that
    means "length is computed by the decode walk" (e.g. a `ModRm`-tagged row, or a row whose `Bytes`
    is the BASE length with a computed-tail flag) does NOT throw the "Byte count mismatch" error. The
    EXACT relaxation: the byte-count equality is enforced for the fixed-length 6502 modes; a row marked
    computed-length is accepted with its base byte count and the equality is skipped. (Scope: this only
    stops the schema from FORBIDDING the row — it does NOT compute the real tail; that is the
    consumer's job, M3.3+.)
  - `A_bare_unknown_mode_still_throws` — a genuinely unknown mode (not a recognized computed-length
    marker) still throws `InvalidDataException` (the vocabulary gate is preserved — the relaxation is
    narrow).

- [ ] **Step 2: Relax the byte-count check** (`OpcodeDataset.cs:133-137`) — gate the
  `d.Bytes == ExpectedBytes(mode)` equality so it applies to the fixed-length modes and a
  computed-length-marked row is accepted without the equality (the minimal not-forbidden change).
  `ExpectedBytes`/`ValidModes` for the 6502 modes are UNCHANGED. Record in the doc comment: this is
  the "do not FORBID a computed length" seam (Ground truth, notion (4)); the importer does not yet
  CONSUME a computed length (no Z80/8086 dataset in M3.1b).

  > **Scope honesty.** No Z80/8086 opcode data is added. This task only ensures the importer's schema
  > validator would not REJECT a future computed-length row — the same "build the seam, defer the real
  > consumer" discipline the fetch-unit (Ground truth D) and the synthetic CPU (Task 7) follow. If the
  > Task 0 grep shows NO importer test actually forbids a computed-length form (i.e. the schema is
  > already permissive enough), this task reduces to the regression guard (`Existing_6502_rows_still_
  > validate`) + a recorded note that no relaxation was needed — a recorded judgement call.

- [ ] **Step 3: Full suite green; `RegeneratedSpecTests` byte-equal** (the 6502 spec + dataset are
  UNCHANGED — Ground truth E). **Commit** —

  ```
  refactor(importer): a computed-length row is not forbidden; 6502 dataset rules unchanged

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

---

## Task 7: The three-property synthetic decode CPU — the M3 thesis made testable (TDD)

> Maps to scope item 6 + Ground truths F + G. The synthetic CPU is the abstraction proof: a
> GENERATOR/JIT fixture (NOT a shipped CPU, NOT the Z80, NOT the 8086) whose spec declares a
> `DecodeStructure` exercising ALL THREE decoder properties the 8086 brief §10.1 names — a PREFIX byte
> (property 1+2), a length-determining mid-stream byte (property 1), and a non-first-byte sub-field key
> (property 3) — plus the fetch-unit word micro-proof (Ground truth D). Modeled on M3.1a's
> `SyntheticRegisterSetTests` precedent. This task ALSO lands the `DecodeStructure` DSL surface + the
> `SpecParser`/`CpuEmitter` wiring that the 6502 leaves default-off (so `Mos6502Spec.cs` stays
> byte-identical).

**Files:** NEW `src/CpuEmulator.Core/Specification/DecodeStructure.cs` (`DecodeStructure`, `PrefixByte`
— default-off); `src/CpuEmulator.Core/Specification/InstructionDef.cs` (add `OperationKey` + the
prefixed/opcode-group carriers); `src/CpuEmulator.Core/Specification/Spec.cs` (the `Insn` overloads —
single-byte UNCHANGED); `src/CpuEmulator.Generators/SpecModel.cs` (`OperationKey` + `KeyShape` on
`InstructionModel`; optional `DecodeStructure`/`FetchUnit` on `SpecModel`); `src/CpuEmulator.Generators/SpecParser.cs`
(parse the optional `DecodeStructure` + the new `Insn` overloads; the single-byte opcode-range check
stays; a CPUGEN for a malformed decode struct); `src/CpuEmulator.Generators/CpuEmitter.cs` (the
non-degenerate `Decode`/`DescriptorFor` emission for a declared `DecodeStructure`); NEW
`tests/CpuEmulator.Tests/Generators/SyntheticDecodeStructureTests.cs`.

- [ ] **Step 1: The DSL surface** (`DecodeStructure.cs` — the Ground truth G literal, verbatim:
  `DecodeStructure(PrefixByte[] Prefixes, byte[] ModRmOpcodes, byte[] SubFieldOpcodes)` + `PrefixByte(byte
  Value)`). Add `Insn` overloads to `Spec.cs` (Ground truth G) — the single-byte form UNCHANGED, plus
  `Insn(byte prefix, byte opcode, …)` and `Insn(byte opcode, int subfield, …)`. Add `uint OperationKey`
  + the carriers to `InstructionDef`; the single-byte ctor sets `OperationKey = opcode`,
  `KeyShape.OpcodeByte` (so the 6502 is byte-identical — every `Mos6502Spec.cs` row uses the single-byte
  form). These are inert syntax carriers (the generator reads them — same posture as the existing
  `InstructionDef` doc).

- [ ] **Step 2: The synthetic spec fixture** (in the new test file — the Ground truth F.1 `DecodeTestSpec`,
  authoritative spelling): registers `A`/`PC`; a `DecodeStructure` declaring prefix `0xCB`, ModRm opcode
  `0x80`, sub-field opcode `0xF6`; the six instruction rows (A) `NOP` 0xEA, (B) `PFXOP` (0xCB,0x10),
  (C) `BARE` 0x10, (D) `MODRMOP` 0x80, (E) `GRP0`/`GRP2` (0xF6, subfield 0/2). The fixture is the
  smallest spec exercising all three properties — arbitrary trivial ops (`NOP`/`Load("A")`); the proof
  is the DECODE SHAPE (key + length), not operand semantics (scope honesty — Ground truth F's "what the
  synthetic CPU does NOT do"). Mirror the M3.1a `SyntheticRegisterSetTests` host pattern
  (`GeneratorTestHost.Run(spec)` + a hand-rolled partial CPU class for any JIT pin).

- [ ] **Step 3: Parser + emitter wiring.** `SpecParser` parses the optional `DecodeStructure` field
  (absent ⇒ the 6502 default, `FetchUnit = Byte`, `KeyShape.OpcodeByte`); parses the new `Insn`
  overloads to set `InstructionModel.OperationKey` + `KeyShape`; the single-byte opcode-range check
  (`SpecParser.cs:352`) stays for single-byte rows; a malformed decode struct (e.g. a prefix that is
  also an opcode with no row) reports a new `CPUGEN` diagnostic (record the next free ID). `CpuEmitter`
  emits the NON-degenerate `Decode` for a declared `DecodeStructure`:
  - a PREFIX byte → peek/consume the prefix unit, then the opcode → `key = (prefix << 8) | opcode`;
  - a `ModRmOpcode` → after the opcode, `LengthRule.ModRmDetermined`: read the next byte,
    `Length = base + (thatByte & mask)` — the COMPUTED length;
  - a `SubFieldOpcode` → read the next byte, `key = (opcode << 3) | ((next >> 3) & 7)`;
  - `DescriptorFor` for a declared structure emits a `Dictionary<uint, OpcodeDescriptor>` (the
    multi-byte key path — Ground truth C); the 6502 (no structure) keeps the dense `[256]` index.

- [ ] **Step 4: The three-property assertions** (`SyntheticDecodeStructureTests` — the Ground truth F.2
  mapping, one test per row):
  - `Property1_MODRMOP_length_is_computed_from_the_midstream_byte` — `Decode([0x80, 0x02, 0x00, 0x00])`
    → `Length == 4`; `Decode([0x80, 0x00])` → `Length == 2`. **Same opcode, different length, by the
    mid-stream byte** (property 1 — length is a COMPUTED output, `8086-…:789-796`).
  - `Property1and2_prefixed_differs_from_unprefixed` — `Decode([0xCB, 0x10])` → `OperationKey ==
    0xCB10`, `Length == 2`, resolves `PFXOP`; `Decode([0x10])` → `OperationKey == 0x10`, `Length == 1`,
    resolves `BARE`. **Prefixed ≠ unprefixed for the same second byte** (`8086-…:798-806`; `0001-…:117-119`).
  - `Property3_subfield_of_a_nonfirst_byte_selects_the_operation` — `Decode([0xF6, 0b00_000_000])`
    resolves `GRP0`; `Decode([0xF6, 0b00_010_000])` resolves `GRP2`. **Same opcode byte, different
    operation, by bits 5-3 of the second byte** (`8086-…:807-810, 740`).
  - `Degenerate_NOP_is_the_6502_walk` — `Decode([0xEA])` → `OperationKey == 0xEA`, `Length == 1`,
    resolves `NOP` (the trivial walk the 6502 always takes — `0001-…:158-159`).
  - `FetchUnit_word_makes_length_two_times_units` — a `BufferFetchStream(unitBytes: 2)` over a 1-unit
    op returns `Length == 2` (Ground truth D's word-unit micro-proof — the walk does not assume bytes;
    `68000-…:747-750`).
  - `Synthetic_spec_generates_a_compiling_class` — `GeneratorTestHost.Run(DecodeTestSpec)` →
    `Assert.Empty(result.AllErrors)` (the abstraction generates clean — the M3.1a precedent posture).

- [ ] **Step 5: The JIT-reachable proof** (Ground truth F.3 — the JIT-side half of property 1):
  - `Discover_advances_by_the_computed_length_over_MODRMOP` — drive the synthetic CPU's decode walk
    through `Discover` (or its decoder-walk core): a run starting at `MODRMOP` (0x80) with mid-stream
    byte `0x02` advances the cursor by 4; mid-stream `0x00` advances by 2. **Proves `Discover` reads
    `r.Length` from the walk, not `d.Length` from a field** (the J3 generalization the 8086 brief calls
    the JIT-ism it "stresses most", `8086-…:686`). *(If wiring a second generated CPU type fully
    through the live `BlockCompiler` proves heavy — same judgement call as M3.1a Task 6 Step 3 — this
    pin may be reduced to driving the generated `Decode` walk directly and asserting `r.Length`,
    recorded; the generator-side property assertions in Step 4 are the load-bearing abstraction proof.)*

- [ ] **Step 6: Confirm the 6502 is UNTOUCHED** — regenerate the 6502: `Mos6502Spec.cs` is
  byte-identical (`RegeneratedSpecTests` green — the 6502 declares NO `DecodeStructure`, uses only the
  single-byte `Insn`); the 6502 generated `Decode`/`DescriptorFor` are the degenerate forms from
  Task 3 (the new wiring is default-off for the 6502). Full suite green. **Commit** —

  ```
  test(generators): synthetic decode CPU proves prefix + ModR/M-length + sub-field key (the M3 thesis)

  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  ```

---

## Task 8: Closeout + UAT — the full pure-refactor gate (NO code change beyond fixes)

> The final gate: the full suite + the heavy `CPUEMULATOR_UAT=full` sweeps that prove the refactor
> changed HOW length is computed, never WHAT it computes for the 6502. Any divergence here is a
> refactor bug, by definition.

- [ ] **Step 1: Full routine suite green** — `dotnet test`; record the actual count (baseline + ~30).
  `dotnet build --no-incremental -warnaserror` clean.
- [ ] **Step 2: The byte-identical-6502 generated-output check** — diff the regenerated
  `Mos6502Cpu.g.cs` against the committed file: the ONLY changes are the enumerated Ground truth E
  regions (the NEW `Decode`/`DescriptorFor`; the `JitDescriptors` `LengthRule.Fixed, FixedLength`
  rows; the `Execute`/`InstructionLength`/`Disassemble` bodies). State fields, `Op{XX}()` bodies,
  `GetRegister`/`SetRegister`, `TryAssemble` byte-identical. `Mos6502Spec.cs` byte-identical
  (`RegeneratedSpecTests`).
- [ ] **Step 3: `CPUEMULATOR_UAT=full` TomHarte — BOTH tiers, interpreter AND through the JIT** —
  151/151 opcodes, 1,510,000 cases per tier, ZERO parity failures, UNCHANGED from baseline.
- [ ] **Step 4: Klaus cycle-exact — BOTH tiers, interpreter AND under the JIT** — reaches `$3469` at
  EXACTLY 96,241,367 cycles, UNCHANGED.
- [ ] **Step 5: Fill the closeout table + the UAT-gate table** (below) with actuals. **No commit unless
  a fix was required**; if the sweeps surfaced a divergence, fix it (a refactor bug), re-run, record.

---

## Self-review

- **Ground truths A–G realized point-by-point.**
  - **(A) `DecodeResult` + the walk contract** — the `DecodeResult(uint OperationKey, int Length,
    DecodedOperands Operands)` / `DecodedOperands` / `KeyShape` value types + `IFetchStream`/`IDecoder`
    land in `Core` (Task 1, verbatim from A.1/A.2); the load-bearing property — `Length =
    UnitsConsumed × UnitBytes`, a COMPUTED output, never a field read — is pinned by
    `Length_equals_units_consumed_times_unit_bytes` (Task 1) and the generated-walk pins (Task 3).
    `BusFetchStream`/`BufferFetchStream` (A.3) are both shipped (Tasks 1, 3).
  - **(B) `OpcodeDescriptor.Length` → `LengthRule` + `FixedLength`** — the static `int Length` is
    removed; the `LengthRule { Fixed, ModRmDetermined }` enum + `FixedLength` carry the walk's input
    (Task 2, verbatim from B); `Discover` advances by the walk's RETURNED length (Task 3's `Discover`
    literal); the run tuple carries `Length`, `PagesSpanned` reads it (Tasks 3, 4). `FixedLength` is
    honestly framed as the easy-case INPUT, not a bypass (B's rationale, carried into Task 2 Step 2).
  - **(C) the opaque operation-key model** — `OperationKey` is an opaque `uint` the generated `Decode`
    packs; the three key shapes (6502 `key == opcode`; prefix `(prefix << 8) | opcode`; sub-field
    `(opcode << 3) | subfield`) are realized in Task 7 Step 3; `DescriptorFor(uint)` encapsulates the
    packing (dense `[256]` for the 6502, dictionary for a declared structure). Non-first-byte sub-field
    proven by `Property3_…` (Task 7 Step 4).
  - **(D) the fetch unit parameterized** — `IFetchStream.UnitBytes` (1 default, 2 word-capable); the
    walk computes `Length = UnitsConsumed × UnitBytes` and never hardcodes `Read8`; the word micro-proof
    is `BufferFetchStream_word_unit_…` (Task 1) + `FetchUnit_word_makes_length_two_times_units`
    (Task 7). Seam built + proven; no shipped word-fetch CPU (68000 is M4) — recorded.
  - **(E) the generated-output delta** — the re-snap (Task 5) captures exactly the enumerated regions;
    the byte-identical regions are spot-pinned (`Interpreter_body_for_LDA_is_unchanged`,
    `TryAssemble_is_unchanged`); the byte-identical-6502 check is Task 8 Step 2. The
    `IMonitorSupport` signatures (`Disassemble`/`InstructionLength`) are UNCHANGED — bodies route
    through the walk (E's note, carried into Task 3 Step 5).
  - **(F) the synthetic test CPU + the three-property mapping** — the `DecodeTestSpec` fixture (F.1) +
    one test per F.2 row (Task 7 Step 4) + the JIT-reachable F.3 proof (Task 7 Step 5). NOT a shipped
    CPU / NOT Z80 / NOT 8086; no real EA computed (F's scope honesty, carried into Task 7 Step 2).
  - **(G) the decode-structure DSL surface** — `DecodeStructure`/`PrefixByte` (default-off) + the
    `Insn` overloads (single-byte UNCHANGED) land in Task 7 Step 1; the 6502 declares none, so
    `Mos6502Spec.cs` is byte-identical (`RegeneratedSpecTests` green — Task 7 Step 6).
- **Placeholder scan: clean.** No `TODO`/`TBD`/`FIXME`/`<placeholder>` in the appended tasks. Every
  literal code block is grounded in a read of the current source (`OpcodeDescriptor.cs`,
  `BlockCompiler.cs:95-151`, `CpuEmitter.cs:98-137, 999-1025, 1247-1344`, `Spec.cs`, `InstructionDef.cs`,
  `OpcodeDataset.cs:120-153`, `SpecModel.cs`, `SpecParser.cs:302-376`); the synthetic spec is the
  authoritative spelling promised by F.1's note ("Task 2's/Task 7's literal code is authoritative").
- **The three-property → synthetic-test mapping confirmed.** Each 8086 brief §10.1 property maps to a
  named test over a named synthetic row: property 1 (computed length) → `Property1_MODRMOP_…` over row
  (D) `MODRMOP`; property 1+2 (prefix/multi-byte) → `Property1and2_prefixed_differs_…` over rows (B)
  `PFXOP`/(C) `BARE`; property 3 (non-first-byte sub-field) → `Property3_subfield_…` over row (E)
  `GRP0`/`GRP2`; degenerate (6502) → `Degenerate_NOP_…` over row (A) `NOP`; fetch-unit → the word
  micro-proof. The JIT-side half of property 1 is the F.3 `Discover_advances_by_the_computed_length_
  over_MODRMOP` pin (Task 7 Step 5). This is the F.2 table made into tests, one-for-one.
- **Authorized-test-changes consistency.** Every appended task that touches an EXISTING test names the
  authorized row it lands: Task 2 lands rows 1-2 (`OpcodeDescriptorTests` ctor migration + the
  `Undefined` sentinel); Task 3 + Task 5 land row 3 (the snapshot re-snap); Task 4 lands row 4 (JIT
  `d.Length` → tuple `Length`); Task 3 Step 5 honors row 5 (the monitor signature is UNCHANGED, only
  bodies route through the walk — most monitor tests are black-box and DO NOT change); Task 6 lands
  row 6 (importer computed-length not forbidden, 6502 rules unchanged). No appended task changes a test
  outside rows 1-6 — anything else turning red is a refactor bug, a STOP (consistent with the
  "Authorized test changes" section's contract).
- **Pure-refactor invariant honored in every gate.** Each task's closing gate is "full suite green";
  Tasks 3/5/6 add the 6502-byte-identical / `RegeneratedSpecTests` checks; Task 8 runs the
  both-tier TomHarte (1.51M × 2, interpreter + JIT) + Klaus (96,241,367 × 2) sweeps + the
  byte-identical generated-output diff. The recorded deviation (length computed, `BaseCycles` stays a
  field) is honored — no task touches cycle logic, so Klaus is unchanged by construction.
- **Recorded judgement calls (not hidden):** Task 6's relaxation may reduce to a regression guard if
  the importer schema is already permissive (the Task 0 grep decides); Task 7 Step 5's full
  live-`BlockCompiler` JIT pin over a second CPU type may reduce to driving the generated `Decode`
  directly (same posture as M3.1a Task 6 Step 3). Both are stated, with the load-bearing pin called
  out as the fallback floor.
- **Sequencing risk (recorded):** Tasks 2 + 3 share the `OpcodeDescriptor` shape — they MUST land
  together (the `Core` type change + its producer/consumers) or the repo has a half-migrated descriptor
  that does not build (called out in both task headers; combined commit recommended).
- **What is deliberately NOT here** (consistent with Scope's NOT-in-scope): no Z80 code, no 8086 code,
  no flag model, no 16-bit register math, no JIT genericity J1, no real word-fetch shipped CPU, no
  cycle-model generalization. Each is named in Scope with its ADR citation and its future chunk.

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| _(Task 1)_ | `DecodeResult`/`IFetchStream`/`IDecoder`/`BufferFetchStream` — the walk contract | _green_ |
| _(Tasks 2+3)_ | `OpcodeDescriptor.Length` → `LengthRule`+`FixedLength`; one generated `Decode`/`DescriptorFor` walk; `Discover` advances by computed length | _green_ |
| _(Task 4)_ | `Discover`/`PagesSpanned` computed-length JIT-reachable pins | _green_ |
| _(Task 5)_ | re-snap the generated decode regions; walk-vs-body length cross-check | _green_ |
| _(Task 6)_ | importer: computed-length not forbidden; 6502 dataset rules unchanged | _green_ |
| _(Task 7)_ | `DecodeStructure` DSL (default-off) + the three-property synthetic decode CPU | _green_ |

**Test count after Task 7:** _(record actual)_ — baseline _(record Task 0 actual)_ + ~30.

### UAT gate (run verbatim; outputs recorded at closeout)

| Gate command | Expected | Actual |
|---|---|---|
| `dotnet build --no-incremental -warnaserror` | 0 warnings, 0 errors | _(record)_ |
| `dotnet test` (routine suite excl. Klaus) | all passing, 0 unexpected skips; count ≈ baseline + ~30 | _(record)_ |
| `CPUEMULATOR_UAT=full` TomHarte (interpreter AND through the JIT, BOTH tiers) | 151/151 opcodes, 1,510,000 cases per tier, ZERO parity failures — UNCHANGED (pure refactor) | _(record)_ |
| Klaus → `$3469` (interpreter AND under the JIT, BOTH tiers) | 96,241,367 cycles EXACTLY — UNCHANGED | _(record)_ |
| Byte-identical-6502 generated output | `Mos6502Cpu.g.cs` changes ONLY the Ground-truth-E regions; `Mos6502Spec.cs` byte-identical (`RegeneratedSpecTests`) | _(record)_ |
| `git grep -n 'd\.Length'` over `src/`+`tools/` | NO static descriptor `Length` reads remain; the walk's computed length is the only length source | _(record)_ |
