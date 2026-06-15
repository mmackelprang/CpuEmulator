# M4.1: Core Register-Width Relaxation + the 68000 Register State (Foundation, Synthetically Proven)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking.

**Goal:** relax the Core register model from `Bits ∈ {8,16}` to `{8,16,32}` (with `uint`-typed backing
fields for 32-bit registers), declare the **68000 register file** (D0–D7, A0–A6, USP/SSP with an A7
mode-selected view, PC, and the SR/CCR split) as a new `Cpus.M68000` spec + hand-written partial, and
prove the whole foundation **synthetically** — all while keeping the 6502 + Z80 generated output
**byte-identical** and their full suites green. **No 68000 instruction executes** (decode/EA/bus/ops are
M4.2+); this PR ships state + the synthetic proof only, mirroring the Z80 "ship the foundation, no opcode
goes live" discipline.

**Architecture:** Three additive Core changes (the generator's three `Bits == 8 ? "byte" : "ushort"`
sites gain a `uint` arm at `Bits >= 17`; the parser's `8 or 16` cap becomes `8 or 16 or 32`; `FlagBitDef.Bit`
accepts 0–15) — each guarded so the 8/16-bit emit path is untouched. The 68000 register file is declared
through the **same dataset→importer→generator→regen pipeline** the Z80 uses (no hand-edited spec), with
USP/SSP as real 32-bit fields, A7 as a mode-selected accessor in the hand-written `M68000Cpu` partial
(the ADR 0001 altitude for mode side effects — the same altitude the Z80's alternate-set swap uses), and
the SR/CCR split modeled via `FlagLayout` (CCR flags in bits 0–4, system-byte bits in 8–15). The
`OperandSize` axis is added to `Core/Specification` as a leaf enum **only** (no op carries it yet) — see
Decision D3. Everything is proven by `GeneratorTestHost.CompileAndLoadType` synthetic fixtures (the
`SyntheticPortIoTests` pattern), plus the `RegeneratedSpecTests` byte-identity guard for the 6502.

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`) that regenerates the per-CPU `*Spec.cs` from a JSON dataset +
semantics map, and xUnit. The 68000 TomHarte gate (SingleStepTests/680x0) is **out of scope** for M4.1
(it arrives with the interpreter, M4.5).

---

## Scope

This PR ships the **register/state FOUNDATION only**. Per ADR 0003 Decision 1 + the ADR 0004 §3 M4.1
boundary, M4.1 delivers the Core changes the whole M4 arc needs for machine state, proven synthetically.

**IN scope:**

1. **Relax `RegisterDef.Bits`** from `8 | 16` to `8 | 16 | 32` (parser validation `SpecParser.cs:497`),
   with the generated backing field typed `uint` for 32-bit registers (the three
   `Bits == 8 ? "byte" : "ushort"` emit sites at `CpuEmitter.cs:71,94,159`). The `ulong`
   `GetRegister`/`SetRegister` introspection contract already fits 32-bit (zero-extend; `SetRegister`
   gets a `uint` cast arm). **The 6502 + Z80 generated output stays byte-identical** (their registers are
   all 8/16-bit, so they never take the new `uint` arm — `RegeneratedSpectests` is the guard).
2. **Relax `FlagBitDef.Bit`** from 0–7 to 0–15 (ADR 0003 Decision 1 consequence) so the 68000's 16-bit
   SR can place the system-byte flags (S/T/I0–I2) in bits 8–15 alongside the CCR flags (X/N/Z/V/C) in
   bits 0–4. Additive — the 6502/Z80 layouts (bits 0–7) are unaffected.
3. **Add the `OperandSize` enum** (`Byte`/`Word`/`Long`) to `Core/Specification` as a standalone type —
   declared, documented, unit-tested, but **not yet threaded onto any `Op`** (Decision D3 explains why
   the size axis lands as a type here and the op-threading is deferred to the first ALU-family PR).
4. **Declare the 68000 register file** as a new `CpuEmulator.Cpus.M68000` project: a generator-clean
   `M68000Spec.cs` (emitted by the importer from a new dataset) declaring D0–D7 (32-bit), A0–A6 (32-bit),
   **USP + SSP** (32-bit, named — not `a7`; the TomHarte schema names `usp`/`ssp`), PC (32-bit), and the
   SR/CCR `FlagLayout`; plus a hand-written `M68000Cpu` partial holding the A7 mode-selected view (the
   S-bit of SR selects USP vs SSP) and the minimal bus/policy hooks the generator requires.
5. **The synthetic proofs:** a synthetic 32-bit-register spec round-trips full 32-bit values through
   `GetRegister`/`SetRegister`; the A7/USP/SSP banking selects correctly by mode; the SR/CCR split
   reads/writes correctly. Plus the `M68000Cpu` itself instantiates, round-trips its registers, and banks
   A7 correctly.

**OUT of scope (each is a later M4 PR — do NOT reach for it):**

- **The wide big-endian bus** (`Read16/Read32/Write16/Write32` + `Endianness` on `IAddressSpace`,
  alignment/address-error) = **M4.2** (parallel with M4.1; different files).
- **Decode + EA** (the word-granular field-decomposed `DecodeStructure` variant, the structured EA
  descriptor, the EA-category legality matrix) = **M4.3**.
- **The 68000 instruction dataset + the field-pattern importer schema + the mnemonic-keyed gzip TomHarte
  loader** = **M4.4**. (M4.1 declares ONLY the register-file rows in a minimal dataset — no `Insn` rows.)
- **The 68000 interpreter + the TomHarte gate** (op bodies, partial-write/sign-extend semantics, EA
  write-back, exceptions, the 2-word prefetch queue, IPL-level interrupts, `STOP`) = **M4.5**.
- **The 68000 through the JIT (all-fallback)** = **M4.6**.
- **Threading `OperandSize` onto any `Op`** (the size-bearing micro-ops `Move68kOp`/`Alu68kOp`/
  `AluAddr68kOp` with partial-write/sign-extend/no-CCR semantics) = **M4.5a** (the first ALU-family PR;
  see Decision D3).
- **A `RegisterRole.BankedStackPointer` understood by the generator** — M4.1 keeps banking entirely in
  the partial (Decision D2); a generated banking role is NOT added.

> **The honest one-liner for M4.1's close-state:** the Core register model accepts 32-bit registers
> (`uint`-typed, full-width through `GetRegister`/`SetRegister`); the 68000 register file (D0–D7, A0–A6,
> USP/SSP, A7-as-mode-view, PC, SR/CCR) exists and is synthetically proven (32-bit round-trip, A7 banking
> by the S-bit, SR/CCR split); the `OperandSize` enum exists in Core. The 6502 + Z80 are byte-identical
> and green. **NO 68000 instruction executes** — there is no decode, no EA, no bus-width, no op body; the
> M68000 dataset has zero `Insn` rows. The 68000 "runs" nothing yet by design.

---

## Decisions (made for M4.1; the ADR left these "just-in-time")

ADR 0003 §4 and ADR 0004 §4 deliberately left several calls to "the first M4 PR." M4.1 is that PR for the
state half. Each decision below is recorded so the Builder does not re-litigate it and the Coordinator can
veto before the run.

### D1 — `Bits == 32` typing: a `uint` field, selected by `Bits >= 17` (additive, byte-identical 8/16)

The generator selects the backing field's C# type from `Bits` at three sites (`CpuEmitter.cs:71` field
decl, `:94` `SetRegister` cast, `:159` PC-local type). Today each is a two-case ternary
`Bits == 8 ? "byte" : "ushort"`. M4.1 replaces each with a small helper `FieldType(int bits)` returning
`"byte"` (≤8), `"ushort"` (≤16), `"uint"` (≤32). **The 8/16 arms are unchanged**, so any register with
`Bits ∈ {8,16}` emits exactly the same text → the 6502/Z80 `.g.cs` is byte-identical (the
`RegeneratedSpectests` guard pins it). 32-bit is the only new arm.

**Why `uint` not `int`:** registers are unsigned containers; `ulong GetRegister` zero-extends a `uint`
cleanly; `SetRegister`'s `unchecked((uint)value)` truncates a `ulong` correctly. (Sign-extension on
`An.w` writes is an *op-body* concern — M4.5 — not a storage-type concern.) This is exactly ADR 0003
Decision 1 Option (A).

### D2 — A7 banking lives in the partial; USP/SSP are real registers; A7 is NOT a generated register

ADR 0003 §4 item 1 left this "just-in-time," recommending the partial. M4.1 takes the recommendation:
- **USP and SSP are declared as real 32-bit registers** in `M68000Spec.cs` (the TomHarte schema names
  `usp`/`ssp`; introspection exposes them by name — the generated `GetRegister`/`SetRegister` cover them).
- **A7 is NOT a spec register.** It is a hand-written property on the `M68000Cpu` partial:
  `public uint A7 { get => SupervisorMode ? SSP : USP; set { if (SupervisorMode) SSP = value; else USP = value; } }`,
  where `SupervisorMode` reads the SR `S`-bit. This is structurally identical to the Z80 pair-view (a
  computed accessor over real backing fields), but hand-written rather than generated because the
  selector is a *mode bit*, not a fixed high/low split — exactly the ADR 0001 altitude for mode side
  effects (the Z80 alternate-set swap, the R-refresh).
- **`RegisterRole` gains NO new member.** No `BankedStackPointer`. The generator stays unaware of
  banking. (If M4.5 finds the generated-introspection-vs-partial-accessor trade-off wants a role, it can
  add one then — but M4.1 does not need it, and adding an unused enum member now is YAGNI.)

**Consequence to flag:** `GetRegister("A7")`/`SetRegister("A7")` will NOT work in M4.1 (A7 is not in the
spec's register-name switch). This is correct for M4.1 — the TomHarte vectors name `usp`/`ssp`, never
`a7`, so the introspection contract never needs "A7" by name. The A7 *property* exists for the eventual
interpreter's stack ops (M4.5) to reference as a C# member. This is a deliberate, recorded asymmetry.

### D3 — `OperandSize` lands as a Core *enum only* in M4.1; op-threading is deferred to M4.5a

ADR 0003 §4 item 2 explicitly flags the precise `Size`-operand placement as "best settled when the first
ALU-family PR has real encodings in hand." M4.1 has **no** ALU encodings (no `Insn` rows at all). Adding
`Move68kOp(OperandSize)`/`Alu68kOp(string, OperandSize)` now would mean adding ops the generator has no
emit arm for, no test exercises, and no dataset produces — dead vocabulary that the first real ALU PR
would likely reshape anyway (the extensible-operand-model question, ADR 0001 J10).

**Decision: M4.1 introduces `public enum OperandSize { Byte, Word, Long }` in
`Core/Specification/OperandSize.cs`** — the size *axis as a type* — with a unit test pinning its members,
and **defers threading it onto any `Op` to M4.5a** (the first 68000 ALU-family PR). Rationale: the type is
genuinely foundational (every M4 size-bearing op and the bus's `AccessWidth` map to it), declaring it now
costs nothing and stakes the name, while the op-shape is data the first ALU PR must hold to get right.
This honors ADR 0003's "make it data, not a code branch" thesis without shipping unexercised op records.

> If the Coordinator prefers the size axis fully threaded onto placeholder ops in M4.1, that is a larger
> PR (it pulls forward part of M4.5a) — flag it; the plan as written keeps M4.1 a pure state/foundation
> PR matching the Z80 e-1a discipline.

### D4 — The 68000 register file goes through the real importer pipeline (no hand-edited spec)

The "dataset→importer→generator→regen pipeline is the law" invariant holds. M4.1 adds a **minimal**
`m68000-opcodes.json` (zero instruction rows — register/flag declarations only, if the dataset format
carries them) + `m68000-semantics.json` (the register table + flag layout), and a new `[CpuSpecification]`
emit path, so `M68000Spec.cs` is generated, not hand-written — and a `RegeneratedSpectests`-style guard
pins it byte-identical to a fresh importer run. (The importer's `SpecFileEmitter` already emits the
`Registers` table + `FlagLayout` from `map.Registers`/`map.Flags` — `SpecFileEmitter.cs:122-149`; M4.1
only needs that path to accept a CPU with no instruction rows.) **If** recon at Task 0 shows the importer
hard-requires ≥1 instruction row or a 6502/Z80-specific code path that a register-only CPU cannot satisfy
cleanly, fall back to a **hand-written-but-guarded** `M68000Spec.cs` with a TODO to fold it into the
importer at M4.4 (when the field-pattern dataset lands) — and record the deviation. The pipeline is
preferred; the fallback is bounded and explicit.

### D5 — The 68000 SR `FlagLayout` names ONLY `Flag`-enum-member flags; the interrupt mask + T are raw SR bits

**New finding beyond the ADRs (the Coordinator needs this).** The parser requires every `FlagLayout` flag
NAME to be a member of the `Flag` enum (`SpecParser.cs:898`: `if (!s_flagMembers.Contains(name))`). The
`Flag` enum (`Flag.cs:13-29`) carries `C/Z/I/D/V/N` (6502) + `S/H/P/Y/X` (Z80). So the 68000's CCR flags
`C/Z/V/N/X` are all valid, and the supervisor `S` bit reuses the `S` member — but **`T` (trace) and the
3-bit interrupt mask `I0/I1/I2` are NOT enum members** (there is an `I` = the 6502 interrupt-DISABLE bit,
which is semantically wrong for the 68000 mask, and no `T`/`I0–I2`).

**Decision: M4.1 declares ONLY the named condition flags in the 68000 `FlagLayout` — `C/V/Z/N/X` (the CCR)
plus `S` (the supervisor bit, the one banking selector the partial reads).** The interrupt mask and the T
bit are modeled as **raw SR bits** read/written through the 16-bit `SR` field directly (they are a
multi-bit field + a trace bit, not single-bit named *condition* flags the flag-writing ops will target by
name). The full 16-bit SR still round-trips losslessly via `SetRegister("SR", …)`/`GetRegister("SR")`
regardless of the `FlagLayout` — the layout only names the bits the eventual flag-emitting ALU ops (M4.5)
reference symbolically.

**Why not add `T`/`I0–I2` to the `Flag` enum now?** YAGNI: M4.1 has no ops that write them by name; the
interrupt mask is consumed as a 3-bit field (the IPL-level comparison, M4.5d), not as three independent
named flags; and the `Flag`-enum membership only matters when a micro-op references a flag by the enum
member. If M4.5 finds the trace/mask bits want enum members (e.g. a `SetFlag(Flag.T, …)`-style op), it can
add them then — additively, the same way the Z80 added `S/H/P/Y/X`. **Flag for the Coordinator:** if you
want the `Flag` enum extended with `T`/`I0–I2` in M4.1 for completeness, that is a small additive change
(it does not perturb the 6502/Z80, whose layouts override per-spec) — but it ships unused vocabulary; the
plan defaults to deferring it.

---

## Ground truth — what the live tree ALREADY provides (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0.**

- **`RegisterDef`** — `src/CpuEmulator.Core/Specification/RegisterDef.cs:12-14`:
  `record RegisterDef(string Name, int Bits, RegisterRole Role = General, string? HighHalf = null,
  string? LowHalf = null)`. The doc comment says "must be 8 or 16" — update it.
- **`RegisterRole`** — `RegisterRole.cs`: `{ General, ProgramCounter, Status, StackPointer }`. No banking
  member (Decision D2 keeps it that way).
- **The parser width cap** — `src/CpuEmulator.Generators/SpecParser.cs:497-502`:
  `if (bits is not (8 or 16))` → diagnostic "register width must be 8 or 16 bits". This is the one
  validation to relax. Note the pair-view validation at `:529-549` skips any register with both
  HighHalf/LowHalf null (`:531-532 continue`), so a 32-bit non-view register passes that block untouched.
- **The generator field-typing sites** — `src/CpuEmulator.Generators/CpuEmitter.cs`:
  - `:71` field decl: `public {(register.Bits == 8 ? "byte" : "ushort")} {register.Name};` (the non-view
    arm; the view arm at `:63-69` is `ushort`-only and 16-bit-only by validation — untouched).
  - `:94` `SetRegister` cast: `string cast = register.Bits == 8 ? "byte" : "ushort";`.
  - `:159` PC-local type: `string pcType = pcRegister.Bits == 8 ? "byte" : "ushort";`.
  - `:80-85` `GetRegister` switch (returns the field directly → `ulong` zero-extends a `uint` for free;
    NO change needed).
- **`RegisterModel`** — `src/CpuEmulator.Generators/SpecModel.cs:58`:
  `record RegisterModel(string Name, int Bits, string Role, string? HighHalf, string? LowHalf)` — `Bits`
  is already an `int`, carries 32 with no model change.
- **`FetchUnit.Word`** — `SpecModel.cs:41`: `enum FetchUnit { Byte, Word }` — **already wired into the
  model but consumed NOWHERE in `CpuEmitter.cs`** (grep confirms: the decode-walk is byte-only). That is
  M4.3's concern, not M4.1's — DO NOT touch it here; just know it exists.
- **`FlagLayout` / `FlagBitDef`** — `src/CpuEmulator.Core/Specification/FlagLayout.cs:10-14`:
  `record FlagLayout(FlagBitDef[] Bits)`, `record FlagBitDef(string Name, int Bit)` — `Bit` is documented
  "0–7 (a byte status register)". The Z80 declares `new FlagLayout([new("S",7), … new("C",0)])`
  (`Z80Spec.cs:60`). The parser/emitter consume `Bit` as a bit-shift; check whether any validation caps
  `Bit ≤ 7` (Task 1 recon).
- **`Flag` enum** — `src/CpuEmulator.Core/Specification/Flag.cs:13-29` — already carries
  `X = 12` (the extend flag; the 68000's second carry). The numeric value is NOT load-bearing when a
  `FlagLayout` is declared (the layout overrides per-spec) — so the 68000's CCR can name `X` and place it
  at CCR bit 4. **No `Flag` enum change is needed** (ADR 0003 Decision 1: "mostly already absorbed").
- **The Z80 register-declaration + partial precedent** — `src/CpuEmulator.Cpus.Z80/Z80Spec.cs:21-60` (the
  `Registers` table with 8/16-bit regs, pair-views via HighHalf/LowHalf, the `FlagLayout`) and
  `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (the hand-written partial: `_bus`/`_io` fields, the ctor, `Reset`,
  the hand-written `Iff1`/`Iff2`/`Q`/`Im` state, `ReadBus`/`WriteBus`, the `partial bool
  InterruptPending`/`Halted`, `partial bool TryServiceInterrupt()`). The `M68000Cpu` partial mirrors this
  shape (minus the I/O bus — the 68000 is von-Neumann with one space).
- **The synthetic-spec test pattern** — `tests/CpuEmulator.Tests/Generators/SyntheticPortIoTests.cs`: a
  `[CpuSpecification]` spec string + a hand-written partial compiled via
  `GeneratorTestHost.CompileAndLoadType(source, "FullType.Name")`, then driven at runtime through
  reflection (`GetField`/`GetMethod`/`Invoke`). `GeneratorTestHost.Run(source)` returns
  `GeneratorDiagnostics`/`AllErrors`/`GeneratedText` for compile-clean assertions. This is the EXACT
  vehicle for every M4.1 synthetic proof. `GeneratorTestHost` is at
  `tests/CpuEmulator.Tests/Generators/GeneratorTestHost.cs:21` (`Run` `:52`, `CompileAndLoadType` `:74`).
- **The byte-identity guard** — `tests/CpuEmulator.Tests/Importer/RegeneratedSpectests.cs`: re-runs the
  importer for the 6502 and asserts the committed `Mos6502Spec.cs` equals the tool output (line-ending
  normalized). M4.1 adds the analogous guard for `M68000Spec.cs` IF Decision D4's pipeline path holds.
- **The importer register-emit** — `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs:122-149`: emits the
  `Registers` table from `map.Registers` (Name, Bits, Role → `RegisterRole.*`, pair → `HighHalf:/LowHalf:`)
  and the `FlagLayout` from `map.Flags`. The role mapping at `:128-134` handles
  StackPointer/Status/ProgramCounter. `SemanticsMap.cs` defines `map.Registers`/`map.Flags`. Task 0 recon
  confirms whether the importer can emit a CPU with zero `Insn` rows.

### RECON FLAGS the implementer MUST re-confirm at Task 0 (the code wins)

> Discovered during write-time recon. Treat the live code as ground truth.

- **R1 — The `FlagBitDef.Bit` cap is at `SpecParser.cs:904`** (CONFIRMED at write-time):
  `if (bit is < 0 or > 7)` → diagnostic "bit N for flag 'X' is outside 0–7". Task 2 relaxes the upper
  bound to `15`. `FlagBitMap.cs` has NO range check (it only stores the dict — `:18-25`), so `:904` is the
  single site. Re-confirm the line at Task 0 (it may have shifted).
- **R1b — `FlagLayout` flag NAMES must be known `Flag` enum members** (`SpecParser.cs:898`:
  `if (!s_flagMembers.Contains(name))` → "'X' is not a known Flag member"). The `Flag` enum
  (`Flag.cs:13-29`) carries `C/Z/I/D/V/N/S/H/P/Y/X` — so the 68000 CCR names `C/Z/V/N/X` and the SR's `S`
  are ALL valid, but `T` and the interrupt-mask bits `I0/I1/I2` are NOT enum members (there is an `I`, the
  6502 interrupt-disable, at value 2 — semantically wrong for the 68000 mask, and there is no `T`/`I0–I2`).
  **This is a NEW finding beyond the ADRs (see "New decision needed" below).** The plan's default
  (Decision D5) declares ONLY the named CCR/CCR-style flags in the `FlagLayout` (`C/Z/V/N/X` + `S`) and
  models the interrupt mask + `T` as raw SR bits read/written through the 16-bit `SR` field directly (they
  are a multi-bit field + a trace bit, not single named condition flags). The full 16-bit SR still
  round-trips via `SetRegister("SR", …)` regardless of the `FlagLayout` — the layout only names the bits
  the eventual flag-writing ops (M4.5) target by name.
- **R2 — Does the generator assume PC ≤ 16-bit anywhere beyond `:159`?** The PC-local `pcType` at `:159`
  is the known site. Grep for other `ushort`-typed PC handling (e.g. relative-jump emit, `(ushort)(PC + …)`
  wraps) — but those live in 6502/Z80 *op* emit arms the 68000 (no ops) never reaches, so they should not
  block M4.1. Confirm the *register-declaration + introspection* path (`:60-99`) is the only path a
  no-instruction 68000 spec exercises.
- **R3 — Can the importer emit a CPU with zero instruction rows?** Decision D4's preferred path. Check
  `SpecFileEmitter.cs` for an assumption of ≥1 row (e.g. an `Instructions` array that must be non-empty,
  a count assertion, a `KeyShape`/decode requirement). If it cannot, take D4's guarded hand-written
  fallback. Re-confirm at Task 5.
- **R4 — Does `GeneratorTestHost.CompileAndLoadType` need the spec's containing project to reference
  anything new for a 32-bit register?** It compiles a source string against the Core + generator
  references. A 32-bit register adds no new reference (it emits `uint`, a built-in). Confirm the host's
  reference set includes `CpuEmulator.Core` (it does — the existing synthetic specs use `RegisterDef`).
- **R5 — Does the `M68000` project need wiring into the host/JIT registry?** The 6502 + Z80 are
  registered somewhere (a CPU factory/registry the host enumerates). M4.1's `M68000Cpu` does NOT need to
  be host-runnable (it executes nothing), so it should NOT be registered into any "runnable CPUs" list
  yet (that would imply it runs). Confirm whether merely *adding the project* forces a registry entry; if
  a registry test enumerates all `[CpuSpecification]` types, ensure the 68000 either satisfies it
  trivially or is explicitly excluded-with-reason. Re-confirm at Task 7.

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | D1: a `FieldType(int bits)` helper; route the three `Bits == 8 ? "byte" : "ushort"` sites (`:71,:94,:159`) through it, adding the `uint` (≤32) arm. |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | D1: relax the width cap `8 or 16` → `8 or 16 or 32` (`:497`); relax the `FlagBitDef.Bit` cap `> 7` → `> 15` (`:904`). |
| `src/CpuEmulator.Core/Specification/RegisterDef.cs` | Modify | D1: update the doc comment ("8, 16, or 32"). |
| `src/CpuEmulator.Core/Specification/FlagLayout.cs` | Modify | D1/R1: update `FlagBitDef.Bit` doc ("0–15; the CCR/system-byte of a 16-bit SR"). |
| `src/CpuEmulator.Core/Specification/OperandSize.cs` | Create | D3: `public enum OperandSize { Byte, Word, Long }` (the size axis as a type; not yet on any Op). |
| `tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterTests.cs` | Create | The 32-bit round-trip proof (synthetic spec; `GetRegister`/`SetRegister` full 32-bit). |
| `tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterByteIdentityTests.cs` | Create | The additivity proof: an 8/16-only synthetic spec's `GeneratedText` has no `uint` field (the width relaxation is additive). |
| `tests/CpuEmulator.Tests/Generators/Sr16BitFlagLayoutTests.cs` | Create | The 16-bit `FlagLayout` proof: a synthetic Status reg with flags in bits 0–4 + 8–15 compiles + the bits read/write correctly. |
| `tests/CpuEmulator.Tests/Core/OperandSizeTests.cs` | Create | D3: pin the `OperandSize` members + values. |
| `src/CpuEmulator.Cpus.M68000/CpuEmulator.Cpus.M68000.csproj` | Create | The new 68000 project (references Core + the generator analyzer, like `CpuEmulator.Cpus.Z80.csproj`). |
| `src/CpuEmulator.Cpus.M68000/AssemblyInfo.cs` | Create | Mirror `Cpus.Z80/AssemblyInfo.cs` (InternalsVisibleTo the test project if the Z80 one does). |
| `tools/CpuEmulator.SpecImporter/data/m68000-opcodes.json` | Create | D4: the minimal 68000 dataset (zero instruction rows). |
| `tools/CpuEmulator.SpecImporter/data/m68000-semantics.json` | Create | D4: the 68000 register table (D0–D7, A0–A6, USP, SSP, PC) + the SR/CCR `FlagLayout`. |
| `tools/CpuEmulator.SpecImporter/*.cs` | Modify (if D4 pipeline) | D4: allow a register-only CPU (zero `Insn` rows); add the m68000 import wiring (the `--out` path). |
| `src/CpuEmulator.Cpus.M68000/M68000Spec.cs` | Create (generated) | The generator-clean register/flag spec (importer output, or guarded hand-write per D4 fallback). |
| `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs` | Create | The hand-written partial: `_bus` + ctor, `Reset`, the A7 mode-view property over USP/SSP, `SupervisorMode` (the SR S-bit), the minimal generator-required hooks (`ReadBus`/`WriteBus`/`HandleUndefinedOpcode`/`TryServiceInterrupt`/`InterruptPending`). |
| `tests/CpuEmulator.Tests/Cpus/M68000RegisterStateTests.cs` | Create | The 68000 register-file proofs: instantiate `M68000Cpu`, round-trip D0–D7/A0–A6/USP/SSP/PC at 32-bit, A7 banks by the S-bit, the SR/CCR split. |
| `tests/CpuEmulator.Tests/Importer/M68000RegeneratedSpecTests.cs` | Create (if D4 pipeline) | The byte-identity guard for `M68000Spec.cs` vs a fresh importer run (mirrors `RegeneratedSpectests`). |
| `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` | Modify | Reference the new `Cpus.M68000` project. |
| `CpuEmulator.slnx` | Modify | Add the new `Cpus.M68000` project to the solution. |
| `docs/architecture/0003-68000-state-width-and-bus.md` | Modify | Tick the M4.1 just-in-time items now decided (D1–D4) with a back-reference to this plan. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502 +
> Z80 byte-identity + their suites staying green), then commit. Tasks are dependency-ordered so the suite
> builds and stays green after every task. Literal code is given for every load-bearing piece. The
> synthetic-spec tests (via `GeneratorTestHost.CompileAndLoadType`) decouple from the real `M68000Spec.cs`
> regen, which lands late (Task 5) — exactly as the Z80 plans did.

### Task 0: Baseline + shipped-surface recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch check + create the work branch.** This is implementation work (it touches
  `src/`), so per the workflow it goes on a branch, not main.
  Run: `git switch -c feat/m4-1-core-width-68000-state`
  Expected: on a fresh branch off `main` (HEAD `6f8d5ab`).

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 0 failures, 0 unexpected skips. Record the EXACT test count (the closeout pins it).
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean (no warnings).

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds** (the "Ground truth" +
  "RECON FLAGS" sections are the checklist):
  - `src/CpuEmulator.Generators/SpecParser.cs:497-502` (the `8 or 16` cap), `:529-549` (the pair-view
    validation skips non-views), and grep the file for any `FlagBitDef`/`Bit` upper-bound check (R1).
  - `src/CpuEmulator.Generators/CpuEmitter.cs:60-99` (the register-decl + `GetRegister`/`SetRegister`
    block — the three typing sites at `:71,:94,:159`), `:159` (PC-local), and grep for other `ushort` PC
    handling (R2).
  - `src/CpuEmulator.Core/Specification/RegisterDef.cs`, `FlagLayout.cs`, `Flag.cs` (confirm `X = 12` is
    present; confirm `FlagBitDef.Bit` has no enforced cap in the type).
  - `src/CpuEmulator.Cpus.Z80/Z80Spec.cs:21-60` + `Z80Cpu.cs` (the register-decl + partial precedent) and
    `src/CpuEmulator.Cpus.Z80/CpuEmulator.Cpus.Z80.csproj` + `AssemblyInfo.cs` (the project shape to mirror).
  - `tests/CpuEmulator.Tests/Generators/SyntheticPortIoTests.cs` + `GeneratorTestHost.cs:21,52,74` (the
    synthetic-spec vehicle).
  - `tests/CpuEmulator.Tests/Importer/RegeneratedSpectests.cs` (the byte-identity guard shape).
  - `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs:122-149` (register/flag emit) + `SemanticsMap.cs`
    (`map.Registers`/`map.Flags` shape) + the importer's entry point (how `--out` selects a CPU) — confirm
    whether zero `Insn` rows is acceptable (R3).
  - Grep for any CPU registry/factory that enumerates `[CpuSpecification]` types or lists runnable CPUs
    (R5) — `grep -rn "CpuSpecification" src/ tests/` and inspect any host registry.

- [ ] **Step 4: Re-derive the 68000 register/flag facts from ADR 0003** (the spec; do NOT trust this
  plan's prose alone). Confirm:
  - D0–D7, A0–A6 are 32-bit data/address registers; USP/SSP are 32-bit; PC is 32-bit (ADR 0003 §1.2).
  - The TomHarte schema names `usp`/`ssp` separately, with **no `a7` field** (ADR 0003 §1.4) — so USP/SSP
    are the introspection names, A7 is a C# convenience view.
  - SR is 16-bit: CCR = low byte (`X N Z V C` in CCR bits 4/3/2/1/0); system byte = high byte
    (`T`=15, `S`=13, interrupt mask `I0–I2`=8–10) (ADR 0003 §1.2). Pin the exact bit positions used in
    Task 5's `FlagLayout` from the ADR.

- [ ] **Step 5:** No commit (read-only). Proceed to Task 1.

---

### Task 1: Relax the parser width cap to `8 | 16 | 32` (TDD)

> The one validation change that lets a 32-bit register reach the emitter. Tested via the generator host
> (a 32-bit-register spec compiles clean instead of emitting CPUGEN diagnostics).

**Files:**
- Modify: `src/CpuEmulator.Generators/SpecParser.cs:497-502` (the width cap)
- Modify: `src/CpuEmulator.Core/Specification/RegisterDef.cs` (doc comment)
- Test: `tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterTests.cs` (create — the "it compiles" half)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterTests.cs`. The first fact asserts a synthetic
  spec with a 32-bit register generates with NO diagnostics (today it errors with "register width must be
  8 or 16 bits"). Mirror the `SyntheticPortIoTests` scaffolding.

```csharp
using System.Reflection;
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M4.1 (ADR 0003 Decision 1) — the 32-bit register proof. A GENERATOR fixture (NOT a shipped
/// CPU) declaring a 32-bit register, compiled via GeneratorTestHost and DRIVEN at runtime: a full 32-bit
/// value round-trips through GetRegister/SetRegister. The 6502/Z80 declare only 8/16-bit registers, so
/// none of this perturbs them (byte-identical .g.cs — proven by SyntheticWideRegisterByteIdentityTests +
/// RegeneratedSpectests).</summary>
public class SyntheticWideRegisterTests
{
    // A minimal synthetic CPU with a 32-bit data register D0, a 32-bit PC, and a 16-bit status. No
    // instructions (the register foundation is what is under test). The partial supplies the bus + hooks
    // the generator requires.
    private const string WideSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("widetest")]
        public static class WideTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32),
                new("SR", 16, RegisterRole.Status),
                new("PC", 32, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class WideTestCpu
        {
            private readonly IAddressSpace _bus;
            public WideTestCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
            private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void Spec_with_a_32bit_register_generates_with_no_diagnostics()
    {
        var result = GeneratorTestHost.Run(WideSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n",
                result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
        // The 32-bit register's backing field is typed `uint` (Task 3 makes this true).
        Assert.Contains("public uint D0;", result.GeneratedText);
        Assert.Contains("public uint PC;", result.GeneratedText);
        // The 16-bit status stays `ushort` (the existing arm is unchanged).
        Assert.Contains("public ushort SR;", result.GeneratedText);
    }
}
```

> **Note on `InstructionDef[] Instructions = []`:** if Task 0 recon (R3) shows the *generator* (as opposed
> to the importer) rejects an empty instruction table, change this to a single benign `Insn(0x4E71, "NOP",
> AddrMode.Implied, [])`-style row using an existing mode — but confirm the generator does NOT require a
> `DecodeStructure` for a single-byte degenerate walk. The generator's degenerate 6502 path
> (`model.Decode is null`) handles a byte-keyed table; a 16-bit-opcode 68000 row is a M4.3 concern, so
> prefer the empty table and only fall back if recon forces it.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~SyntheticWideRegisterTests"`
  Expected: FAIL — the generator emits a CPUGEN diagnostic "register width must be 8 or 16 bits" for `D0`
  and `PC` (the cap rejects 32). (The `uint` assertions also fail, but the diagnostic is the first wall.)

- [ ] **Step 3: Relax the width cap.** In `src/CpuEmulator.Generators/SpecParser.cs:497`, change:

```csharp
            if (bits is not (8 or 16))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register width must be 8 or 16 bits"));
                continue;
            }
```
  to:
```csharp
            // M4.1 (ADR 0003 Decision 1): widen the register-width cap to admit 32-bit registers (the
            // 68000's D0–D7/A0–A6/USP/SSP/PC). 8/16 are unchanged, so the 6502/Z80 emit byte-identically
            // (the field-type selection's 8/16 arms are untouched — CpuEmitter FieldType()).
            if (bits is not (8 or 16 or 32))
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register width must be 8, 16, or 32 bits"));
                continue;
            }
```

- [ ] **Step 4: Update the `RegisterDef` doc comment.** In
  `src/CpuEmulator.Core/Specification/RegisterDef.cs:3`, change "must be 8 or 16 (wider registers arrive
  with a 16/32-bit CPU)" to "must be 8, 16, or 32 (32-bit arrived with the 68000, M4.1)".

- [ ] **Step 5: Run the test.** It still FAILS on the `uint` assertions (the emitter has no 32-bit arm
  yet — Task 3) but the **diagnostic is now gone**. Confirm the failure message is the missing-`uint`
  assertion, NOT a generator diagnostic.
  Run: `dotnet test --filter "FullyQualifiedName~SyntheticWideRegisterTests"`
  Expected: FAIL on `Assert.Contains("public uint D0;", …)` — the field is still emitted as `ushort` (the
  truncating `Bits == 8 ? "byte" : "ushort"` makes any >8 register `ushort`). This is the bridge to Task 3.

  > **Sequencing note:** this task's test cannot fully pass until Task 3 adds the `uint` emit arm. That is
  > intentional — Task 1 removes the *validation* wall, Task 3 adds the *typing*. Keep the test red on the
  > `uint` assertion; it goes green in Task 3 Step 4. (If you prefer a strictly-green-per-task discipline,
  > split the test: a `…_generates_with_no_diagnostics_for_width` fact that asserts only
  > `result.GeneratorDiagnostics.IsEmpty` for the no-diagnostic half passes here; the `uint`-field facts
  > land in Task 3. The plan keeps one fact for brevity; the implementer may split.)

- [ ] **Step 6: Full gate.**
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpectests"` → green (the 6502 cap-relaxation
  is additive; no 6502 register is 32-bit, so its `.g.cs` is unchanged).
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Generators/SpecParser.cs \
        src/CpuEmulator.Core/Specification/RegisterDef.cs \
        tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterTests.cs
git commit -m "$(cat <<'EOF'
feat(core): relax RegisterDef.Bits cap to 8|16|32 (the 68000 register width)

The emitter still types >8-bit registers as ushort; Task 3 adds the uint arm.
The 6502/Z80 stay byte-identical (no 32-bit register; the 8/16 path is unchanged).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1 (grows in Task 3).

---

### Task 2: Relax `FlagBitDef.Bit` to 0–15 (TDD)

> The 68000's SR is 16-bit (CCR low byte + system byte). `FlagLayout` must accept bit positions 0–15.
> The cap is at `SpecParser.cs:904` (`bit is < 0 or > 7`). Tested via a synthetic spec whose Status
> register places the `S` flag at bit 13 (above the old cap), compiling clean.
>
> **Flag-name constraint (R1b, CONFIRMED `SpecParser.cs:898`):** `FlagLayout` names must be `Flag`-enum
> members. The synthetic spec below uses ONLY enum members (`C/V/Z/N/X` + `S`) — `S` at bit 13 is the
> load-bearing "above bit 7" case. Do NOT name `T`/`I0`/`I1`/`I2` (not enum members; Decision D5 keeps the
> 68000 interrupt-mask + trace bits as raw `SR` bits, not named flags).

**Files:**
- Modify: `src/CpuEmulator.Generators/SpecParser.cs:904` (relax `bit is < 0 or > 7` → `> 15`)
- Modify: `src/CpuEmulator.Core/Specification/FlagLayout.cs` (doc comment)
- Test: `tests/CpuEmulator.Tests/Generators/Sr16BitFlagLayoutTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/Sr16BitFlagLayoutTests.cs`. A synthetic spec declares a 16-bit
  Status register `SR` with a `FlagLayout` placing the CCR flags in bits 0–4 and the `S` flag at bit 13.
  The fact asserts the spec generates with no diagnostics (today it FAILS at `SpecParser.cs:904` —
  "bit 13 for flag 'S' is outside 0–7").

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M4.1 (ADR 0003 Decision 1) — the 16-bit SR FlagLayout proof. The 68000's status register is
/// 16-bit: the CCR (X N Z V C) in bits 0–4, plus the supervisor (S) bit at 13. FlagBitDef.Bit must accept
/// 0–15 (the cap at SpecParser.cs:904 was 0–7). A synthetic spec placing the S flag above bit 7 must
/// compile clean. (The behavioral read/write proof — that the SR/CCR split round-trips — is the M68000
/// register-state test, Task 6.) Only Flag-enum-member names are used (C/V/Z/N/X + S); the interrupt
/// mask + T bit are raw SR bits, not named flags (Decision D5).</summary>
public class Sr16BitFlagLayoutTests
{
    private const string SrSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("srtest")]
        public static class SrTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("D0", 32),
                new("SR", 16, RegisterRole.Status),
                new("PC", 32, RegisterRole.ProgramCounter),
            ];

            // CCR (low byte): C=0 V=1 Z=2 N=3 X=4. Supervisor (S) bit at 13 — the "above bit 7" case.
            public static readonly FlagLayout Flags = new([
                new("C", 0), new("V", 1), new("Z", 2), new("N", 3), new("X", 4),
                new("S", 13)]);

            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class SrTestCpu
        {
            private readonly IAddressSpace _bus;
            public SrTestCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
            private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void Spec_with_flags_above_bit_7_generates_with_no_diagnostics()
    {
        var result = GeneratorTestHost.Run(SrSpec);

        Assert.True(result.GeneratorDiagnostics.IsEmpty,
            "generator diagnostics: " + string.Join("\n",
                result.GeneratorDiagnostics.Select(d => d.Id + ": " + d.GetMessage())));
        Assert.Empty(result.AllErrors);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Sr16BitFlagLayoutTests"`
  Expected: FAIL — the generator emits `InvalidFlagLayout` "bit 13 for flag 'S' is outside 0–7"
  (`SpecParser.cs:904`).

- [ ] **Step 3: Relax the `Bit` cap.** In `src/CpuEmulator.Generators/SpecParser.cs:904`, change:

```csharp
            if (bit is < 0 or > 7)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidFlagLayout,
                    element.GetLocation(), $"bit {bit} for flag '{name}' is outside 0–7"));
                return ImmutableArray<FlagBitModel>.Empty;
            }
```
  to:
```csharp
            // M4.1 (ADR 0003 Decision 1): the 68000's SR is 16-bit (CCR bits 0–4, system byte 8–15), so a
            // flag bit position may be 0–15 (was 0–7 for a byte status register). The Z80's 0–7 layout is
            // unchanged.
            if (bit is < 0 or > 15)
            {
                diagnostics.Add(new DiagnosticInfo(SpecDiagnostics.InvalidFlagLayout,
                    element.GetLocation(), $"bit {bit} for flag '{name}' is outside 0–15"));
                return ImmutableArray<FlagBitModel>.Empty;
            }
```

- [ ] **Step 4: Update the `FlagBitDef` doc comment.** In
  `src/CpuEmulator.Core/Specification/FlagLayout.cs:12-14`, change "`Bit` is 0–7 (a byte status register)"
  to "`Bit` is 0–15 (0–7 for a byte status register; the 68000's 16-bit SR uses 0–4 for the CCR and 8–15
  for the system byte, M4.1)".

- [ ] **Step 5: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Sr16BitFlagLayoutTests"`
  Expected: PASS.

- [ ] **Step 6: Full gate.**
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpectests"` → green (the Z80's 0–7 layout is
  unchanged; the 6502 declares no `FlagLayout`).
  Run: `dotnet test --filter "FullyQualifiedName~FlagLayoutTests"` → green (the existing Z80 flag-layout
  tests still pass).
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Generators/SpecParser.cs \
        src/CpuEmulator.Core/Specification/FlagLayout.cs \
        tests/CpuEmulator.Tests/Generators/Sr16BitFlagLayoutTests.cs
git commit -m "$(cat <<'EOF'
feat(core): accept FlagBitDef.Bit positions 0-15 (the 68000 16-bit SR)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1.

---

### Task 3: Add the `uint` field-type arm via a `FieldType(int)` helper (TDD)

> The load-bearing generator change. The three sites that pick a register's C# type from `Bits` route
> through one helper that returns `byte`/`ushort`/`uint`. The 8/16 arms are byte-identical; `uint` is the
> only new output. This turns the 32-bit synthetic spec's fields into `uint` and makes Task 1's full test
> green.

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (the helper + the three call sites `:71,:94,:159`)
- Test: `tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterTests.cs` (extend — the round-trip half)

- [ ] **Step 1: Extend the failing test.** Add the runtime round-trip facts to
  `SyntheticWideRegisterTests.cs` (the class from Task 1). These drive the compiled CPU: set a full 32-bit
  value via `SetRegister`, read it back via `GetRegister`, and assert the full 32 bits survive (a `ushort`
  field would truncate to 16).

```csharp
    private static readonly Lazy<Type> s_cpu =
        new(() => GeneratorTestHost.CompileAndLoadType(WideSpec, "SyntheticCpu.WideTestCpu"));

    private static object NewCpu()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 24);
        bus.MapMemory(0, new byte[0x1000], writable: true);
        return Activator.CreateInstance(s_cpu.Value, bus)!;
    }

    private static ulong Get(object cpu, string r) =>
        (ulong)s_cpu.Value.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;
    private static void Set(object cpu, string r, ulong v) =>
        s_cpu.Value.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });

    [Fact]
    public void D0_round_trips_a_full_32bit_value()
    {
        var cpu = NewCpu();
        Set(cpu, "D0", 0xDEADBEEFul);
        Assert.Equal(0xDEADBEEFul, Get(cpu, "D0"));   // a ushort field would give 0xBEEF
    }

    [Fact]
    public void PC_round_trips_a_full_32bit_value()
    {
        var cpu = NewCpu();
        Set(cpu, "PC", 0x00FF_FFFEul);                // a 24-bit-ish PC value, > 16 bits
        Assert.Equal(0x00FF_FFFEul, Get(cpu, "PC"));
    }

    [Fact]
    public void SetRegister_truncates_to_the_field_width_for_a_32bit_register()
    {
        var cpu = NewCpu();
        Set(cpu, "D0", 0x1_2345_6789ul);              // 33 bits in; uint field keeps the low 32
        Assert.Equal(0x2345_6789ul, Get(cpu, "D0"));
    }
```

  Add the required usings at the top of the file: `using CpuEmulator.Core;` (for `AddressSpace`/
  `AddressSpaceKind`/`IAddressSpace`). Confirm `AddressSpace` admits `addressBits: 24` (ADR 0002: the
  24-bit address fits the flat page table — the existing cap is 24).

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~SyntheticWideRegisterTests"`
  Expected: FAIL — `D0_round_trips…` gets `0xBEEF` (the field is `ushort`, truncating), and the
  `Assert.Contains("public uint D0;", …)` from Task 1 still fails.

- [ ] **Step 3: Add the `FieldType` helper + route the three sites.** In
  `src/CpuEmulator.Generators/CpuEmitter.cs`, add a private static helper (place it near `EmitBody`):

```csharp
    /// <summary>M4.1 (ADR 0003 Decision 1): the register backing-field C# type as a function of width.
    /// 8 → byte, 16 → ushort, 32 → uint. The 8/16 arms are UNCHANGED from the prior two-case ternary, so
    /// every 6502/Z80 register (all 8/16-bit) emits byte-identically (RegeneratedSpectests guards it). The
    /// uint arm is the only new output and is reached ONLY by a register declaring Bits == 32 — a
    /// construct the 6502/Z80 specs never use. The parser caps Bits at {8,16,32}, so no other width
    /// reaches here.</summary>
    private static string FieldType(int bits) => bits <= 8 ? "byte" : bits <= 16 ? "ushort" : "uint";
```

  Then change the three sites:
  - `:71` (field decl):
```csharp
                sb.AppendLine($"    public {FieldType(register.Bits)} {register.Name};");
```
  - `:94` (`SetRegister` cast):
```csharp
            string cast = FieldType(register.Bits);
```
  - `:159` (PC-local type):
```csharp
        string pcType = FieldType(pcRegister.Bits);
```

  > **Byte-identity reasoning:** `FieldType(8) == "byte"`, `FieldType(16) == "ushort"` — exactly what the
  > old ternary produced. The emitted text for any 8/16-bit register is character-for-character identical.
  > Only `Bits == 32` produces new text (`uint`). The 6502/Z80 declare no 32-bit register, so their
  > `.g.cs` is unchanged. This is the additive guarantee.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~SyntheticWideRegisterTests"`
  Expected: PASS — D0/PC round-trip full 32-bit values; `SetRegister` truncates a 33-bit input to 32;
  the `uint`/`ushort` `GeneratedText` assertions (Task 1) hold.

- [ ] **Step 5: The additivity guard test.** Create
  `tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterByteIdentityTests.cs`: a synthetic spec with
  ONLY 8/16-bit registers (no 32-bit) whose `GeneratedText` contains no `uint` field declaration — proving
  the relaxation did not perturb the 8/16 path.

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

/// <summary>M4.1 — the additivity guard at the synthetic level (the 6502/Z80 RegeneratedSpectests are the
/// real CPUs' guard). A spec declaring ONLY 8/16-bit registers must emit NO `uint` field — the width
/// relaxation is purely additive; the 8/16 arms are unchanged.</summary>
public class SyntheticWideRegisterByteIdentityTests
{
    private const string NarrowSpec = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace SyntheticCpu;

        [CpuSpecification("narrowtest")]
        public static class NarrowTestSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("F", 8, RegisterRole.Status),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];
            public static readonly InstructionDef[] Instructions = [];
        }

        public sealed partial class NarrowTestCpu
        {
            private readonly IAddressSpace _bus;
            public NarrowTestCpu(IAddressSpace bus) { _bus = bus; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
            private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
            private void HandleUndefinedOpcode(byte op) { _cycles++; }
            private partial bool TryServiceInterrupt() => false;
            public partial bool InterruptPending => false;
        }
        """;

    [Fact]
    public void An_8_16_only_spec_emits_no_uint_field()
    {
        var result = GeneratorTestHost.Run(NarrowSpec);

        Assert.Empty(result.AllErrors);
        Assert.Contains("public byte A;", result.GeneratedText);
        Assert.Contains("public ushort PC;", result.GeneratedText);
        Assert.DoesNotContain("public uint ", result.GeneratedText);
    }
}
```

- [ ] **Step 6: Run it.**
  Run: `dotnet test --filter "FullyQualifiedName~SyntheticWideRegisterByteIdentityTests"`
  Expected: PASS.

- [ ] **Step 7: Full gate — the byte-identity guards are the keystone here.**
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpectests"` → green (6502 byte-identical).
  Run: `dotnet test` → full suite green, INCLUDING every Z80 generator test + the Z80 TomHarte/ZEX suites
  (the Z80 is the live proof that 8/16 emit is unchanged). If any Z80 test regresses, the `FieldType`
  routing changed the 8/16 output — `superpowers:systematic-debugging` it; the helper must return exactly
  `"byte"`/`"ushort"` for 8/16.
  Run: `dotnet build --no-incremental -warnaserror` → clean.

- [ ] **Step 8: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterTests.cs \
        tests/CpuEmulator.Tests/Generators/SyntheticWideRegisterByteIdentityTests.cs
git commit -m "$(cat <<'EOF'
feat(core): emit uint backing fields for 32-bit registers (FieldType helper)

The three Bits->C#-type sites route through FieldType(int): byte/ushort/uint.
The 8/16 arms are unchanged, so the 6502/Z80 .g.cs is byte-identical
(RegeneratedSpectests + the full Z80 suite are green).

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~5 (4 in `SyntheticWideRegisterTests` + 1 byte-identity).

---

### Task 4: Add the `OperandSize` Core enum (TDD)

> The size axis as a type (Decision D3). Declared + pinned by a unit test; NOT threaded onto any Op yet.

**Files:**
- Create: `src/CpuEmulator.Core/Specification/OperandSize.cs`
- Test: `tests/CpuEmulator.Tests/Core/OperandSizeTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create `tests/CpuEmulator.Tests/Core/OperandSizeTests.cs`:

```csharp
using CpuEmulator.Core.Specification;
using Xunit;

namespace CpuEmulator.Tests.Core;

/// <summary>M4.1 (ADR 0003 Decision 1, Decision D3) — the OperandSize axis exists as a Core type. The
/// 68000's .b/.w/.l size suffix is a property of the (instruction × micro-op), not of the register
/// declaration. M4.1 declares the type and stakes the name; threading it onto the size-bearing ops
/// (Move68kOp/Alu68kOp/AluAddr68kOp) is deferred to the first ALU-family PR (M4.5a), when real encodings
/// settle the operand-model shape (ADR 0003 §4 item 2).</summary>
public class OperandSizeTests
{
    [Fact]
    public void OperandSize_has_byte_word_long_members()
    {
        Assert.Equal(0, (int)OperandSize.Byte);
        Assert.Equal(1, (int)OperandSize.Word);
        Assert.Equal(2, (int)OperandSize.Long);
        Assert.Equal(3, System.Enum.GetValues<OperandSize>().Length);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~OperandSizeTests"`
  Expected: FAIL — `OperandSize` does not compile (type not found).

- [ ] **Step 3: Create the enum.** Create `src/CpuEmulator.Core/Specification/OperandSize.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>The 68000 operand-size axis (.b/.w/.l), M4.1 (ADR 0003 Decision 1). The size is a property of
/// the (instruction × micro-op), NOT of the register declaration: the SAME D0 is operated on at three
/// widths by the instruction, with partial-write semantics for data registers (.b/.w preserve the upper
/// bits) and whole-register-sign-extend for address registers (An.w writes all 32 bits, sign-extended).
///
/// M4.1 declares the type and stakes the name; it is NOT yet threaded onto any <see cref="Op"/>. The
/// size-bearing micro-ops (the 68000's Move/ALU family, with the partial-write / sign-extend / no-CCR-on-
/// An semantics in the op body) arrive with the first 68000 ALU-family PR (M4.5a), when real encodings
/// settle the extensible-operand-model shape (ADR 0003 §4 item 2 left this just-in-time). Byte/Word/Long
/// map naturally onto <see cref="CpuEmulator.Core.AccessWidth"/> (1/2/4) for the wide bus (M4.2).</summary>
public enum OperandSize
{
    Byte,
    Word,
    Long,
}
```

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~OperandSizeTests"`
  Expected: PASS.

- [ ] **Step 5: Full gate.**
  Run: `dotnet build --no-incremental -warnaserror` → clean (a new unreferenced enum adds no warnings).
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpectests"` → green (no generated output
  changed — the enum is not consumed by the generator yet).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Core/Specification/OperandSize.cs \
        tests/CpuEmulator.Tests/Core/OperandSizeTests.cs
git commit -m "$(cat <<'EOF'
feat(core): add the OperandSize (.b/.w/.l) axis enum

Declared as a Core type; not yet threaded onto any Op. The size-bearing
68000 ops arrive with the first ALU-family PR (M4.5a) per ADR 0003 §4 item 2.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1.

---

### Task 5: Create the `Cpus.M68000` project + the generator-clean `M68000Spec.cs` (TDD)

> The 68000 register file as a real spec, via the importer pipeline (Decision D4). The spec declares
> D0–D7, A0–A6, USP, SSP, PC (32-bit), and the SR/CCR `FlagLayout` — NO instruction rows. A `RegeneratedSpec`
> guard pins it byte-identical to a fresh importer run.

**Files:**
- Create: `src/CpuEmulator.Cpus.M68000/CpuEmulator.Cpus.M68000.csproj`
- Create: `src/CpuEmulator.Cpus.M68000/AssemblyInfo.cs`
- Create: `tools/CpuEmulator.SpecImporter/data/m68000-opcodes.json`
- Create: `tools/CpuEmulator.SpecImporter/data/m68000-semantics.json`
- Modify (if D4 pipeline): `tools/CpuEmulator.SpecImporter/*` (allow zero `Insn` rows; wire the m68000 out-path)
- Create (generated): `src/CpuEmulator.Cpus.M68000/M68000Spec.cs`
- Modify: `CpuEmulator.slnx` (add the project)
- Create: `tests/CpuEmulator.Tests/Importer/M68000RegeneratedSpecTests.cs`
- Modify: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj` (reference the new project)

- [ ] **Step 1: Decide the D4 path from Task 0 recon (R3).** If the importer accepts a register-only CPU
  (zero `Insn` rows), take the pipeline path (Steps 2–7). If it hard-requires instruction rows or a
  6502/Z80-specific path, take the guarded hand-write fallback (Step 8) and record the deviation in the
  closeout. **Default: pipeline.**

- [ ] **Step 2: Create the project.** Create
  `src/CpuEmulator.Cpus.M68000/CpuEmulator.Cpus.M68000.csproj` mirroring
  `src/CpuEmulator.Cpus.Z80/CpuEmulator.Cpus.Z80.csproj` (read it first — copy the `<TargetFramework>`,
  the `ProjectReference` to `CpuEmulator.Core`, and the analyzer `ProjectReference` to
  `CpuEmulator.Generators` with `OutputItemType="Analyzer" ReferenceOutputAssembly="false"`). Create
  `src/CpuEmulator.Cpus.M68000/AssemblyInfo.cs` mirroring `Cpus.Z80/AssemblyInfo.cs` (if it has
  `InternalsVisibleTo` the test assembly, replicate it). Add the project to `CpuEmulator.slnx`.

- [ ] **Step 3: Author the dataset + semantics.** Create
  `tools/CpuEmulator.SpecImporter/data/m68000-semantics.json` declaring the register table + the SR/CCR
  flag layout (the exact JSON shape comes from Task 0 recon of `SemanticsMap.cs` — match the Z80's
  `z80-semantics.json` register/flag schema). The registers (ADR 0003 §1.2; bits per Task 0 Step 4):

  - `D0`–`D7`: 32-bit, role General.
  - `A0`–`A6`: 32-bit, role General. (A7 is NOT declared — Decision D2.)
  - `USP`: 32-bit, role General. `SSP`: 32-bit, role **StackPointer** (the spec needs one StackPointer
    role; SSP is the supervisor stack the reset vector loads — Task 0 confirms whether the parser requires
    exactly-one StackPointer; if it tolerates zero or one, role SSP as StackPointer and USP as General).
  - `PC`: 32-bit, role ProgramCounter (exactly one — the parser requires it, `SpecParser.cs:551-555`).
  - `SR`: 16-bit, role Status.
  - `FlagLayout` (Decision D5 — `Flag`-enum members ONLY; the parser rejects unknown names at
    `SpecParser.cs:898`): CCR `C=0 V=1 Z=2 N=3 X=4` plus `S=13` (the supervisor bit the partial's banking
    reads). **Do NOT declare `T`/`I0`/`I1`/`I2`** — they are not `Flag` enum members; the trace bit + the
    3-bit interrupt mask are modeled as raw `SR` bits (the full 16-bit `SR` round-trips via
    `SetRegister("SR", …)` regardless of the layout). Confirm `C/Z/V/N/X/S` are all in `s_flagMembers`
    (they are — `Flag.cs:13-29` carries `C/Z/V/N` + `X`(=12) + `S`(=8); the numeric values are NOT
    load-bearing once a `FlagLayout` is declared — the layout overrides per-spec, exactly as the Z80 does).

  Create `tools/CpuEmulator.SpecImporter/data/m68000-opcodes.json` as a **valid-but-empty** instruction
  dataset (the schema `OpcodeDataset` expects, with zero opcode entries). Confirm the loader tolerates an
  empty array at Task 0/Step 4 recon.

- [ ] **Step 4: Wire the importer (if needed).** If the importer's entry point selects a CPU by name/flag,
  add the m68000 case so:
  `dotnet run --project tools/CpuEmulator.SpecImporter -- --dataset …/m68000-opcodes.json --semantics …/m68000-semantics.json --out src/CpuEmulator.Cpus.M68000/M68000Spec.cs`
  emits a `[CpuSpecification("m68000")] public static class M68000Spec` with the `Registers` table +
  `FlagLayout` + an empty `Instructions` array. If `SpecFileEmitter` asserts ≥1 emitted row or emits a
  `DecodeStructure` unconditionally, gate those on a non-empty instruction set (additive; the 6502/Z80
  have rows so their output is unchanged — confirm via the 6502/Z80 regen guards).

- [ ] **Step 5: Write the failing byte-identity test.** Create
  `tests/CpuEmulator.Tests/Importer/M68000RegeneratedSpecTests.cs` mirroring `RegeneratedSpectests.cs`:

```csharp
using System.IO;
using CpuEmulator.SpecImporter;
using Xunit;

namespace CpuEmulator.Tests.Importer;

/// <summary>M4.1 — the 68000 register-file byte-identity guard (mirrors RegeneratedSpectests for the
/// 6502). The committed M68000Spec.cs must equal a fresh importer run (line-ending normalized). The 68000
/// spec declares ONLY the register file + SR/CCR FlagLayout — zero instruction rows (M4.1 is state-only;
/// decode/EA/ops are M4.3+).</summary>
public class M68000RegeneratedSpecTests
{
    [Fact]
    public void Committed_M68000Spec_is_exactly_the_tool_output()
    {
        const string datasetRelPath   = "tools/CpuEmulator.SpecImporter/data/m68000-opcodes.json";
        const string semanticsRelPath = "tools/CpuEmulator.SpecImporter/data/m68000-semantics.json";

        string repoRoot = FindRepoRoot();
        var dataset = OpcodeDataset.Load(Path.Combine(repoRoot, datasetRelPath));
        var map     = SemanticsMap.Load(Path.Combine(repoRoot, semanticsRelPath));

        var (source, report) = SpecImportEngine.Run(dataset, map, datasetRelPath, semanticsRelPath);

        string committed = File.ReadAllText(
            Path.Combine(repoRoot, "src/CpuEmulator.Cpus.M68000/M68000Spec.cs"));

        Assert.Equal(source.Replace("\r\n", "\n"), committed.Replace("\r\n", "\n"));
        Assert.Equal(0, report.Emitted);   // zero instruction rows in M4.1 (state-only)
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CpuEmulator.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
```

  > Confirm `SpecImportEngine.Run`'s signature (the 6502 test calls it with relpaths; match it). If
  > `report.Emitted` is not the right field for "zero rows," use whatever count field the report exposes
  > (Task 0 recon of `ImportReport`). The byte-identity equality is the load-bearing assertion.

- [ ] **Step 6: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000RegeneratedSpecTests"`
  Expected: FAIL — `M68000Spec.cs` does not exist yet.

- [ ] **Step 7: Regenerate `M68000Spec.cs` + reference the project from the tests.**
  Run:
```bash
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset tools/CpuEmulator.SpecImporter/data/m68000-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/m68000-semantics.json \
  --out src/CpuEmulator.Cpus.M68000/M68000Spec.cs
```
  Add a `<ProjectReference>` to `CpuEmulator.Cpus.M68000` from
  `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`. The generated `M68000Spec.cs` will NOT compile on
  its own yet (the generator needs the `M68000Cpu` partial's bus/hooks — Task 6 supplies them). That is
  fine for THIS test, which runs the importer in-process (it does not compile the generated CPU). But the
  SOLUTION build will break until Task 6 — so run the byte-identity test in isolation here:
  Run: `dotnet test --filter "FullyQualifiedName~M68000RegeneratedSpecTests"`
  Expected: PASS (the committed file equals the tool output).

  > **Build-break window:** between Task 5 and Task 6 the `Cpus.M68000` project does not compile (no
  > partial). Keep Tasks 5+6 as a tight pair; do NOT run the full `dotnet build` between them. If the
  > subagent-driven runner gates each task on a full build, MERGE Tasks 5 and 6 into one task (author the
  > spec + the partial together, then build once). The plan presents them separately for review clarity;
  > the implementer may fuse them.

- [ ] **Step 8 (FALLBACK, only if R3 forces it): hand-write `M68000Spec.cs` with a guard.** If the
  importer cannot emit a register-only CPU, hand-write `src/CpuEmulator.Cpus.M68000/M68000Spec.cs`
  matching the EXACT shape the importer would produce (copy the Z80 spec's header/structure; declare the
  registers + FlagLayout + empty Instructions). Add a `// TODO(M4.4): fold into the importer when the
  field-pattern dataset lands` and SKIP the `M68000RegeneratedSpecTests` (mark it `Skip = "M4.4: importer
  pipeline"`). Record the deviation in the closeout for the Coordinator. **This is the bounded fallback;
  prefer the pipeline.**

- [ ] **Step 9: Commit** (paired with Task 6 if fused — otherwise commit the spec + dataset + project now;
  the solution build is green again after Task 6).

```bash
git add src/CpuEmulator.Cpus.M68000/ \
        tools/CpuEmulator.SpecImporter/data/m68000-opcodes.json \
        tools/CpuEmulator.SpecImporter/data/m68000-semantics.json \
        tools/CpuEmulator.SpecImporter/ \
        tests/CpuEmulator.Tests/Importer/M68000RegeneratedSpecTests.cs \
        tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj \
        CpuEmulator.slnx
git commit -m "$(cat <<'EOF'
feat(m68000): declare the 68000 register file spec (D0-D7/A0-A6/USP/SSP/PC + SR-CCR)

Generator-clean via the importer pipeline; zero instruction rows (state-only, M4.1).
A7 banking + the partial land in the next commit.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1 (the regen guard).

---

### Task 6: The hand-written `M68000Cpu` partial — A7 banking + SR/CCR + the register-state proofs (TDD)

> The hand-written half: the bus + ctor, `Reset`, the A7 mode-view property over USP/SSP, `SupervisorMode`
> (the SR S-bit), and the generator-required hooks. Proven by the 68000 register-state tests: 32-bit
> round-trip of every register, A7 banks by the S-bit, the SR/CCR split.

**Files:**
- Create: `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs`
- Test: `tests/CpuEmulator.Tests/Cpus/M68000RegisterStateTests.cs` (create)

- [ ] **Step 1: Write the failing tests.** Create
  `tests/CpuEmulator.Tests/Cpus/M68000RegisterStateTests.cs`. These reference the REAL `M68000Cpu` type
  (not a synthetic spec — this is the shipped 68000), so they need the `Cpus.M68000` project reference
  (added in Task 5 Step 7). Drive the CPU through `GetRegister`/`SetRegister` and the `A7`/`SR` members.

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.M68000;
using Xunit;

namespace CpuEmulator.Tests.Cpus;

/// <summary>M4.1 — the 68000 register-file proof (ADR 0003 Decision 1). The register state exists and is
/// correct: D0–D7/A0–A6/USP/SSP/PC round-trip full 32-bit values; A7 is a mode-selected VIEW over USP/SSP
/// (the SR S-bit selects); the SR/CCR split reads/writes. NO instruction executes — this is state only.</summary>
public class M68000RegisterStateTests
{
    private static M68000Cpu NewCpu()
    {
        var bus = new AddressSpace(AddressSpaceKind.Program, 24);
        bus.MapMemory(0, new byte[0x10000], writable: true);
        return new M68000Cpu(bus);
    }

    [Theory]
    [InlineData("D0")] [InlineData("D7")]
    [InlineData("A0")] [InlineData("A6")]
    [InlineData("USP")] [InlineData("SSP")] [InlineData("PC")]
    public void Register_round_trips_a_full_32bit_value(string reg)
    {
        var cpu = NewCpu();
        cpu.SetRegister(reg, 0xDEAD_BEEFul);
        Assert.Equal(0xDEAD_BEEFul, cpu.GetRegister(reg));   // a ushort field would truncate to 0xBEEF
    }

    [Fact]
    public void A7_is_not_a_named_introspection_register()
    {
        // Decision D2: A7 is a C# convenience view, NOT a spec register. The TomHarte schema names
        // usp/ssp, never a7, so GetRegister("A7") is intentionally unknown.
        var cpu = NewCpu();
        Assert.Throws<System.ArgumentException>(() => cpu.GetRegister("A7"));
    }

    [Fact]
    public void A7_reads_USP_in_user_mode()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(false);                 // user mode (SR.S = 0)
        cpu.SetRegister("USP", 0x0001_0000ul);
        cpu.SetRegister("SSP", 0x0008_0000ul);
        Assert.Equal(0x0001_0000u, cpu.A7);           // A7 == USP in user mode
    }

    [Fact]
    public void A7_reads_SSP_in_supervisor_mode()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(true);                  // supervisor mode (SR.S = 1)
        cpu.SetRegister("USP", 0x0001_0000ul);
        cpu.SetRegister("SSP", 0x0008_0000ul);
        Assert.Equal(0x0008_0000u, cpu.A7);           // A7 == SSP in supervisor mode
    }

    [Fact]
    public void Writing_A7_in_user_mode_targets_USP_only()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(false);
        cpu.SetRegister("SSP", 0x0008_0000ul);
        cpu.A7 = 0x0002_0000u;
        Assert.Equal(0x0002_0000ul, cpu.GetRegister("USP"));   // USP got the write
        Assert.Equal(0x0008_0000ul, cpu.GetRegister("SSP"));   // SSP untouched
    }

    [Fact]
    public void Writing_A7_in_supervisor_mode_targets_SSP_only()
    {
        var cpu = NewCpu();
        cpu.SetSupervisorMode(true);
        cpu.SetRegister("USP", 0x0001_0000ul);
        cpu.A7 = 0x0009_0000u;
        Assert.Equal(0x0009_0000ul, cpu.GetRegister("SSP"));   // SSP got the write
        Assert.Equal(0x0001_0000ul, cpu.GetRegister("USP"));   // USP untouched
    }

    [Fact]
    public void SupervisorMode_reflects_the_SR_S_bit()
    {
        var cpu = NewCpu();
        cpu.SetRegister("SR", 0x2000ul);              // S-bit (bit 13) set
        Assert.True(cpu.SupervisorMode);
        cpu.SetRegister("SR", 0x0000ul);              // S-bit clear
        Assert.False(cpu.SupervisorMode);
    }

    [Fact]
    public void SR_CCR_split_round_trips()
    {
        var cpu = NewCpu();
        // SR = 0x271F: S(13)+I2,I1,I0(10,9,8 = mask 7) in the system byte; CCR low byte 0x1F = X N Z V C all set.
        cpu.SetRegister("SR", 0x271Ful);
        Assert.Equal(0x271Ful, cpu.GetRegister("SR"));        // the full 16-bit SR round-trips
        Assert.Equal((byte)0x1F, cpu.Ccr);                    // the CCR is the SR low byte
    }
}
```

  > If `SetSupervisorMode`/`Ccr` are not the member names you prefer, adjust both the test and the partial
  > (Step 3) in lockstep — the names must match (self-review type-consistency check). The plan uses
  > `SupervisorMode` (get), `SetSupervisorMode(bool)` (an explicit setter that writes the SR S-bit so the
  > test does not depend on SR-bit-layout knowledge), and `Ccr` (the SR low byte as a `byte`).

- [ ] **Step 2: Run the tests to verify they fail.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000RegisterStateTests"`
  Expected: FAIL — `M68000Cpu` has no partial (no ctor, no `A7`/`SupervisorMode`/`Ccr`), so it does not
  compile.

- [ ] **Step 3: Write the `M68000Cpu` partial.** Create `src/CpuEmulator.Cpus.M68000/M68000Cpu.cs`,
  mirroring the Z80 partial's shape (`Z80Cpu.cs`) minus the I/O bus (the 68000 is von-Neumann, one space):

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Cpus.M68000;

/// <summary>The MINIMAL hand-written half of the 68000 (M4.1) — the bus wiring, the A7/USP/SSP banking,
/// the SR/CCR accessors, and the policy hooks the generated partial requires. This is the STATE
/// FOUNDATION: it makes the generated register file compile and proves the register model synthetically
/// (32-bit round-trip, A7 banking by the SR S-bit, the SR/CCR split). It is NOT an interpreter: there is
/// NO decode, NO EA, NO op body, NO wide bus, NO prefetch queue, NO exception/vector machinery — those are
/// M4.2–M4.5. `Step` exists (the generator emits it) but the instruction table is empty, so it fetches a
/// non-existent opcode and routes to HandleUndefinedOpcode; M4.1 never calls Step. The interrupt hooks are
/// inert (the IPL-level model is M4.5d).</summary>
public sealed partial class M68000Cpu
{
    // The single program/data bus (von Neumann; the 68000 has no separate I/O space — IO is memory-mapped).
    // M4.1 wires Read8/Write8 (the byte path); the wide big-endian Read16/Read32 are M4.2.
    private readonly IAddressSpace _bus;

    /// <summary>The supervisor-stack-bit mask in the 16-bit SR (bit 13). The S-bit selects which physical
    /// stack A7 aliases (USP when clear, SSP when set). Pinned here so the banking logic does not depend on
    /// the FlagLayout's declared bit (the layout names S=13; this constant must match it — guarded by the
    /// SupervisorMode_reflects_the_SR_S_bit test).</summary>
    private const ushort SrSupervisorBit = 1 << 13;

    public M68000Cpu(IAddressSpace bus)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
    }

    /// <summary>True when the SR supervisor (S) bit is set. Selects the SSP bank for A7; the eventual
    /// exception machinery (M4.5d) toggles it on entry/RTE.</summary>
    public bool SupervisorMode => (SR & SrSupervisorBit) != 0;

    /// <summary>Set/clear the SR supervisor (S) bit. A test/host convenience for M4.1 (the real toggle is
    /// the exception/RTE sequence in M4.5d). Keeps the banking tests independent of SR-bit-layout knowledge.</summary>
    public void SetSupervisorMode(bool supervisor) =>
        SR = (ushort)(supervisor ? (SR | SrSupervisorBit) : (SR & ~SrSupervisorBit));

    /// <summary>The Condition Code Register — the low byte of the 16-bit SR (X N Z V C). The 68000's
    /// user-visible flags; the system byte (interrupt mask, S, T) is the SR high byte.</summary>
    public byte Ccr
    {
        get => (byte)(SR & 0xFF);
        set => SR = (ushort)((SR & 0xFF00) | value);
    }

    /// <summary>A7 — the stack pointer, BANKED into USP/SSP by the SR S-bit (ADR 0003 Decision 1). NOT a
    /// spec register (Decision D2): the TomHarte schema names usp/ssp, never a7, so introspection exposes
    /// USP/SSP by name and A7 is this C# convenience view (the same altitude as the Z80 pair-views, but
    /// mode-selected rather than high/low-split, so hand-written rather than generated). The implicit
    /// stack ops of exceptions/BSR/JSR/RTS (M4.5) reference A7; privileged MOVE USP reaches the other bank.</summary>
    public uint A7
    {
        get => SupervisorMode ? SSP : USP;
        set { if (SupervisorMode) SSP = value; else USP = value; }
    }

    /// <summary>Reset — M4.1 stub (the real reset reads the initial SSP + PC from the vector table at
    /// addresses 0/4 via the wide bus; that is M4.5). Clears the cycle count implicitly via the generated
    /// state; sets nothing else (the harness sets registers explicitly in the M4.5 TomHarte runner).</summary>
    public void Reset() { }

    // ── The policy hooks the generated partial requires (inert in M4.1) ────────────────────────────────
    public void SetIrqLine(bool asserted) { }   // the IPL-level interrupt model is M4.5d
    public void SetNmiLine(bool asserted) { }   // the 68000 has no NMI line; level-7 is non-maskable (M4.5d)

    /// <summary>Program/data-bus byte read; charges one cycle (the cycle invariant). The wide big-endian
    /// Read16/Read32 the 68000 truly needs are M4.2 (this byte path keeps the generated Step compiling).</summary>
    private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
    private void WriteBus(uint address, byte value) { _cycles++; _bus.Write8(address, value); }

    /// <summary>Undefined-opcode hook — M4.1 stub (the 68000's illegal-instruction exception is M4.5d). The
    /// instruction table is empty in M4.1, so any Step would route here; M4.1 never calls Step.</summary>
    private void HandleUndefinedOpcode(byte opcode) { _cycles++; }

    /// <summary>No interrupt servicing in M4.1 (the IPL-level policy is M4.5d). Returns false so the
    /// generated Step never vectors.</summary>
    private partial bool TryServiceInterrupt() => false;

    /// <summary>Never pending in M4.1 (the IPL-level + SR-mask comparison is M4.5d).</summary>
    public partial bool InterruptPending => false;
}
```

  > **Generator-required surface:** confirm against the generated `M68000Spec.g.cs` (from Task 5) which
  > hooks the generated partial actually references — the Z80 needs `ReadBus`/`WriteBus`/
  > `HandleUndefinedOpcode`/`TryServiceInterrupt`/`InterruptPending`, and (only if the spec has a HaltOp)
  > `Halted`/`IdleCycle`/`DoHalt`. The M4.1 68000 spec has NO HaltOp (no ops), so it needs NEITHER the
  > halt members NOR `OnInstructionFetched`/`OnInterruptEnable` (those are emitted only for specific op
  > kinds). If the build complains about a missing member, add the minimal inert stub the generated code
  > names — do NOT add members the generator does not reference (dead code). `SR`, `USP`, `SSP` are
  > generated fields (from the spec); the partial references them as members of the same class.

- [ ] **Step 4: Run the tests to verify they pass.**
  Run: `dotnet test --filter "FullyQualifiedName~M68000RegisterStateTests"`
  Expected: PASS — all register round-trips, A7 banking both directions, `SupervisorMode`, the SR/CCR
  split. If `A7_is_not_a_named_introspection_register` fails (i.e. `GetRegister("A7")` does NOT throw),
  recon shows A7 leaked into the spec — remove it from the dataset (Decision D2).

- [ ] **Step 5: Full gate — the whole solution now builds.**
  Run: `dotnet build --no-incremental -warnaserror` → clean (the `Cpus.M68000` project compiles now that
  the partial exists).
  Run: `dotnet test` → full suite green, INCLUDING the 6502/Z80 byte-identity guards + their TomHarte/ZEX
  suites (the 68000 addition is additive; it touches no 6502/Z80 file).
  Run: `dotnet test --filter "FullyQualifiedName~M68000RegeneratedSpecTests"` → green (the spec is
  byte-identical to the importer; the partial does not change the spec).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.M68000/M68000Cpu.cs \
        tests/CpuEmulator.Tests/Cpus/M68000RegisterStateTests.cs
git commit -m "$(cat <<'EOF'
feat(m68000): the M68000Cpu partial — A7/USP/SSP banking + SR/CCR + register-state proof

A7 is a mode-selected view over USP/SSP (the SR S-bit selects); USP/SSP are the
introspection names (the TomHarte schema names usp/ssp, not a7). 32-bit round-trip,
A7 banking, and the SR/CCR split are synthetically proven. No instruction executes.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~9 (7 facts + 1 theory with 7 cases counted as the theory + the A7-not-named fact).

---

### Task 7: ADR tick + close-state honesty + the full-suite confirmation (docs + gate)

> Tick the ADR 0003 just-in-time items M4.1 decided (D1–D4), confirm the 68000 is NOT registered as a
> runnable CPU (R5), and pin the close-state.

**Files:**
- Modify: `docs/architecture/0003-68000-state-width-and-bus.md` (tick the decided just-in-time items)

- [ ] **Step 1: Confirm the 68000 is not falsely "runnable" (R5).** From Task 0 recon: if a host/registry
  enumerates `[CpuSpecification]` types or a "runnable CPUs" list, confirm the 68000 is either trivially
  satisfied (it has a valid spec) WITHOUT being presented as executable, or explicitly excluded. If a
  registry test fails because it tries to *run* the 68000 (Step the empty table), exclude the 68000 from
  that test with a reason (`// M4.1: the 68000 has no instructions yet — decode/ops are M4.3+`). The 68000
  must NOT appear in any "this CPU executes programs" surface. Record what you found.

- [ ] **Step 2: Tick the ADR just-in-time items.** In `docs/architecture/0003-68000-state-width-and-bus.md`
  §4, append a resolution note to items 1 and 2 (and a back-reference to this plan):

```markdown
> **M4.1 resolution (2026-06-15, `docs/superpowers/plans/2026-06-15-m4-1-core-width-and-68000-state.md`):**
> - Item 1 (A7 banking): RESOLVED — USP/SSP are real 32-bit registers; A7 is a hand-written mode-selected
>   property on the M68000Cpu partial (the SR S-bit selects). RegisterRole gains NO banking member.
> - Item 2 (OperandSize placement): the enum landed in Core (M4.1); threading it onto the size-bearing ops
>   is deferred to the first ALU-family PR (M4.5a) per the ADR's own recommendation.
> - Item 3 (prefetch-queue timing): unchanged — deferred to the interpreter PR (M4.5), out of M4.1 scope.
```

- [ ] **Step 3: Final full gate (the close-state).**
  Run: `dotnet test` → full suite green. Record the EXACT count (the closeout pins it; it grew by ~18 new
  M4.1 tests over the Task 0 baseline).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpectests"` → 6502 byte-identical.
  Run: the Z80 TomHarte sweep (the live 8/16 proof):
  `CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"` → green (the width
  relaxation did not perturb the Z80).

- [ ] **Step 4: Commit.**

```bash
git add docs/architecture/0003-68000-state-width-and-bus.md
git commit -m "$(cat <<'EOF'
docs(adr): tick the M4.1-decided just-in-time items (A7 banking, OperandSize)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

- [ ] **Step 5: Open the PR.** Per the workflow, open a PR from `feat/m4-1-core-width-68000-state` to
  `main`. The PR body's close-state section asserts: Core accepts 32-bit registers (`uint`-typed,
  full-width through `GetRegister`/`SetRegister`); `FlagBitDef.Bit` accepts 0–15; the `OperandSize` enum
  exists; the 68000 register file (D0–D7, A0–A6, USP/SSP, A7-as-mode-view, PC, SR/CCR) is declared +
  synthetically proven (32-bit round-trip, A7 banking by the S-bit, the SR/CCR split); the 6502 + Z80 are
  byte-identical + green; **NO 68000 instruction executes** (the M68000 dataset has zero `Insn` rows;
  decode/EA/bus-width/ops are M4.2+). Include the Docs Impact (the ADR tick) + the new-test count.

**New-test estimate:** 0 (docs + gate).

---

## Scope note — why this is ONE PR (not split)

ADR 0004 §3 flags M4.1 as "likely splits if the size-axis op-model + the banking each prove large." In
M4.1 as scoped here, **neither is large**: the size axis is a leaf enum (Decision D3 defers the op-model),
and the banking is a ~15-line hand-written property (Decision D2 keeps it in the partial). The whole PR is
three additive generator/parser one-liners + an enum + a register-only spec + a thin partial + their
synthetic proofs. It does not warrant a split. If the Coordinator pulls the `OperandSize` op-threading
forward (overriding D3), THAT would justify splitting M4.1a (width + state) from M4.1b (size-axis ops) —
flag it before the run.

## Self-review

- **Spec coverage (the task prompt's M4.1 scope):**
  - "Relax `RegisterDef.Bits` 8|16 → 8|16|32, `uint` field for 32-bit, `ulong` API fits" → Tasks 1 + 3.
  - "6502 + Z80 byte-identical" → the `FieldType` 8/16 arms unchanged (Task 3); guarded by
    `RegeneratedSpectests` + the byte-identity synthetic (Task 3 Step 5) + the full Z80 suite (every task's
    gate).
  - "Declare the 68000 register file (D0–D7, A0–A6, USP+SSP named, A7 mode-view, PC, SR/CCR split)" →
    Tasks 5 (spec) + 6 (partial + A7 view + SR/CCR).
  - "OperandSize call (in M4.1 or deferred) + why" → Decision D3 + Task 4 (enum in M4.1; op-threading
    deferred to M4.5a, reasoned).
  - "Prove synthetically: 32-bit round-trip; A7 banks by mode; SR/CCR reads/writes" → Tasks 3 (synthetic
    32-bit) + 6 (the real 68000: round-trip, A7 banking both directions, SR/CCR split).
  - "FlagBitDef.Bit 0–7 → 0–15 (the X-flag/SR layout)" → Task 2 (+ the `Flag` enum already carries `X`,
    confirmed; no `Flag` change needed).
  - "Honest close-state: state exists + proven; NO 68000 instruction executes" → the one-liner + Task 7
    Step 5 PR body + the zero-`Insn`-rows guard (Task 5).
- **Placeholder scan:** every code step shows literal code; no "TBD"/"add error handling"/"similar to Task
  N". The two recon-conditional steps (R1's flag-bit cap, R3's importer path) give BOTH branches
  explicitly with a default. None reference an undefined type/member.
- **Type consistency:** `FieldType(int)` is defined once (Task 3) and used at three sites. `A7` (uint
  prop), `USP`/`SSP` (generated uint fields), `SR` (generated ushort field), `SupervisorMode` (bool get),
  `SetSupervisorMode(bool)`, `Ccr` (byte prop) are named identically in the Task 6 test and the Task 6
  partial. `OperandSize { Byte, Word, Long }` (Task 4) matches the test (Task 4) and the ADR-tick (Task 7).
  `M68000RegeneratedSpecTests`/`SpecImportEngine.Run` mirror the 6502 `RegeneratedSpectests` exactly.

---

## Reporting (for the Builder's closeout)

End-of-PR, report: the exact `RegisterDef.Bits`→`uint` change (the `FieldType(int)` helper routing the
three `CpuEmitter.cs:71/:94/:159` sites; the parser cap relaxation `SpecParser.cs:497`) + how the 8/16
arms stayed byte-identical (the `RegeneratedSpectests` + full-Z80-suite evidence); the 68000 register-file
model (D0–D7/A0–A6 General, USP General/SSP StackPointer, PC ProgramCounter, SR Status; A7 the
hand-written mode-view; the SR/CCR FlagLayout bit positions used); the `OperandSize` call (enum-only in
M4.1, op-threading → M4.5a) + why; the D4 path actually taken (importer pipeline vs. the guarded
hand-write fallback) + any deviation; and the final test count + the byte-identity + Z80-green evidence.
