# M3.4e-1a: The Z80 IX/IY Framework — `Indexed` AddrMode + the `(IX+d)` EA helper + IXh/IXl half-views

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended)
> or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax
> for tracking. This is the FIRST of the two M3.4e-1 framework slices (e-1a = the addressing-mode +
> register-half + EA machinery; e-1b = the compound-prefix decoder). Land e-1a, then e-1b, BEFORE the
> opcode slices M3.4e-2/3.

**Goal:** add the addressing-mode + register-file machinery the IX/IY planes need — the `Indexed` AddrMode
(`(IX+d)`/`(IY+d)`), its `JitMode.Indexed` mirror, the four mirror-table entries that make `Indexed` a
declarable mode, the IXh/IXl/IYh/IYl 8-bit half-views of IX/IY (D2), and a reusable `(IX+d)`/`(IY+d)`
effective-address emit helper — **WITHOUT making any DD/FD opcode live**. e-1a is proven entirely with
SYNTHETIC tests; NO `dd *.json` vector turns green here (that is M3.4e-2/3). Every 6502 artifact stays
byte-identical; the whole Z80 (base + CB + ED + block) stays TomHarte-green at the universal Q/WZ/IM bar.

**Architecture:** the Z80 base + CB + ED-core + ED-block planes are TomHarte-green (PR #22 + M3.4c/d). ADR
0001 Decision 3 already declares IX/IY as 16-bit registers; the addressing-mode table (`Indexed`) is the
one mode the M3.4 enumerated finding flagged as still-OUT (`SpecFileEmitter.cs:56-57`: "Indexed (IX+d,
M3.4c) stays OUT — its rows keep emitting // TODO(mode)"). This slice flips it IN. It is **additive and
analogous** to how M3.4a added `Register`/`RegisterIndirect`/`ExtendedAddress` and M3.4b added `Bit`: a new
`AddrMode` member, its mirror in the three importer/parser tables + `JitMode`, a `ModeLength`/
`ClassifyForJit` arm, and a small composable emit helper that every indexed emit arm (M3.4e-2/3) will call.
The IXh/IXl half-views reuse the EXISTING pair-view machinery (`CpuEmitter.cs:63-69` — the HL→H/L pattern),
with one structural nuance recorded as RECON-FINDING A1 below (the storage inversion). Every 6502 artifact
stays byte-identical.

**Tech Stack:** C# (.NET 10), a Roslyn incremental source generator (`CpuEmulator.Generators`), a console
spec importer (`CpuEmulator.SpecImporter`) that regenerates `Z80Spec.cs` from `z80-opcodes.json` +
`z80-semantics.json`, and xUnit + the SingleStepTests/z80 vectors (TomHarte).

---

## Scope

**IN scope (the addressing + register-file framework; NO opcode goes live):**

1. **The `Indexed` AddrMode member** + its mirrors: `AddrMode.cs` (the enum), `SpecParser.cs` `s_addrModes`,
   `SpecFileEmitter.cs` `SupportedModes`, `OpcodeDescriptor.cs` `JitMode`. Plus the `ModeLength("Indexed")`
   arm (3 bytes: prefix + opcode + displacement) and the `ClassifyForJit`/`JitBaseCycles` treatment
   (Z80 → JIT fallback, like every other Z80 mode).
2. **The IXh/IXl/IYh/IYl 8-bit half-views (D2).** Declare `IXh`/`IXl`/`IYh`/`IYl` as 8-bit registers and
   convert `IX`/`IY` to pair-VIEWS over them (the H/L→HL pattern, inverted — see RECON-FINDING A1). In
   `z80-semantics.json` + the regenerated `Z80Spec.cs`. The undocumented IXh/IXl OPS are M3.4e-2; e-1a only
   lays the register-file half-views so those ops have a target to name.
3. **The `(IX+d)`/`(IY+d)` effective-address emit helper** — `EmitZ80IndexedEa(sb, indexReg)` emitting
   `ushort __ea = unchecked((ushort)(<indexReg> + (sbyte)<displacement-byte>));` — usable by every indexed
   emit arm (M3.4e-2/3). e-1a ships the helper + a synthetic test proving it computes a signed EA; no live
   opcode calls it yet.

**OUT of scope (later slices — do NOT reach for them):**

- **The compound-prefix DECODER** (`PrefixByte`/`DecodeStructure` extension + `EmitStructuredDecodeWalk`
  compound routing + the synthetic `DD CB d op` decode-walk test) = **M3.4e-1b** (the immediately-next
  slice). e-1a does NOT touch `DecodeStructure.cs` or the decode walk.
- **Any DD/FD/DDCB/FDCB opcode going live** (the indexed `(IX+d)` re-interpretation, the IX/IY 16-bit ops,
  the undoc IXh/IXl ops, the compound bit/rotate/shift) = M3.4e-2/3. e-1a adds NO dataset rows and makes NO
  `dd *.json` vector green.
- **Interrupt SERVICING / ZEXALL / the JIT IL** = M3.5.

> **The honest one-liner for M3.4e-1a's close-state:** the `Indexed` AddrMode + its `JitMode.Indexed`
> mirror are declarable; IXh/IXl/IYh/IYl exist as 8-bit half-views of IX/IY (IX/IY are now computed pair-
> views, storage moved to the halves) and round-trip through GetRegister/SetRegister; a reusable `(IX+d)`/
> `(IY+d)` signed-EA emit helper exists and is synthetically proven. NO DD/FD opcode is live and NO
> `dd *.json` vector is asserted green — that is M3.4e-2/3, which depend on the e-1b compound decoder. The
> whole Z80 (base + CB + ED + block) stays TomHarte-green at the universal Q/WZ/IM bar; every 6502 artifact
> is byte-identical.

---

## Ground truth — what M3.4a/b/c/d ALREADY shipped (read before drafting any edit)

**Confirm each by reading the cited file:line at Task 0** — e-1a REUSES or EXTENDS them.

- **The AddrMode vocabulary + its three mirrors.** `src/CpuEmulator.Core/Specification/AddrMode.cs` lists
  21 members ending at `Bit` (`:21`); `Indexed` is ABSENT. The mirrors that MUST grow in concert (the
  "SYNC HAZARD" the files call out): `src/CpuEmulator.Generators/SpecParser.cs:164-174` (`s_addrModes`) and
  `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs:49-60` (`SupportedModes` — `:56-57` literally says
  "Indexed (IX+d, M3.4c) stays OUT — its rows keep emitting // TODO(mode)"). The JIT-side mirror is
  `src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs:20-33` (`JitMode`, ending at `Bit`). The emitter writes
  `CpuEmulator.Core.Jit.JitMode.{insn.Mode}` LITERALLY (`CpuEmitter.cs:3218,3250`), so a mode in
  `SupportedModes` that is NOT a `JitMode` member fails to compile — all four tables move together.
- **`ModeLength`** — `src/CpuEmulator.Generators/CpuEmitter.cs:2815-2827`: the per-mode byte count
  (`"RelativeJump" or "Bit" => 2`, `"ExtendedAddress" => 3`, etc.) with a `_ => throw` default. A new
  `Indexed` mode reaching this switch without an arm THROWS at generation. `Indexed` is **3 bytes**
  (prefix + opcode + displacement) for the DD/FD core forms — but see RECON-FINDING A2: the DDCB compound
  form is 4 bytes and is handled by e-1b's decode walk, NOT `ModeLength`; e-1a's `ModeLength("Indexed")`
  arm covers the DD/FD-core 3-byte case (the only `Indexed`-mode rows e-2 adds; the DDCB rows are a
  compound-key shape e-1b handles).
- **The pair-view register machinery.** `src/CpuEmulator.Generators/CpuEmitter.cs:61-72`: a `RegisterDef`
  with `HighHalf`/`LowHalf` set emits a COMPUTED property (`get => (ushort)((high << 8) | low); set { high
  = …; low = …; }`) with NO backing field — "the halves are the only storage" (`:64-66`). A `RegisterDef`
  WITHOUT halves emits a backing field (`:71`). `GetRegister`/`SetRegister` (`:80-99`) switch on the
  register NAME and work for both shapes. `z80-semantics.json:29-36` declares AF/BC/DE/HL/AF_/… as views
  (`"highHalf": …, "lowHalf": …`) over the 8-bit halves declared FIRST (`:25-26` declare IX/IY as bare
  16-bit, NO halves).
- **The `Z80IndirectPair` + `EmitWz` helper shape** — `src/CpuEmulator.Generators/CpuEmitter.cs:553`
  (`EmitWz(sb, $"{Z80IndirectPair(instruction.Opcode)} + 1")`) + the `EmitWz` body (M3.4c, `WZ =
  unchecked((ushort)(<expr>));`). The new `EmitZ80IndexedEa` helper is the SAME shape — a small static that
  appends one C# statement string — so the indexed emit arms (M3.4e-2/3) read the EA from a known local.
- **The runner ALREADY sets + checks IX/IY (RECON-FINDING A3 — the scoped plan's flag was stale).**
  `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunner.cs:46` sets `cpu.SetRegister("IX", s.Ix);
  cpu.SetRegister("IY", s.Iy);` and `:64` checks `Check(problems, cpu, "IX", f.Ix, 4); Check(problems,
  cpu, "IY", f.Iy, 4);`. `Z80TomHarteCase.cs:84` parses `U16(e, "ix")`/`U16(e, "iy")`. **So NO runner
  change is needed in e-1a** (or e-1b). Task 0 Step 3 RE-CONFIRMS this; if for any reason it regressed, add
  the set/check then — but recon shows it is already present and exercised by every base/CB/ED case (which
  carry init.ix == final.ix, trivially passing).
- **The JIT predicates** — `ClassifyForJit` (`CpuEmitter.cs:3310-3358`): every Z80 class is a JIT FALLBACK
  (`bool z80 = …; bool fallback = … || z80;`) and maps `jitClass => "Register"`. `JitBaseCycles`,
  `JitOpLiteral`. A new mode does NOT add a class, so `ClassifyForJit` is unaffected by `Indexed` per se —
  but `DescriptorLiteral`/`KeyedDescriptorLiteral` interpolate `JitMode.{insn.Mode}`, so `JitMode.Indexed`
  MUST exist before any `Indexed`-mode row is emitted (e-2). e-1a adds the member; no row uses it yet.
- **The synthetic-spec test host** — `tests/CpuEmulator.Tests/Generators/GeneratorTestHost.cs`:
  `CompileAndLoadType(source, typeName)` + `Run(source)` (returns `GeneratorDiagnostics`/`GeneratedText`).
  The M3.4d deviation #1: structured synthetic fixtures use `IAddressSpace _bus` (NOT a raw `byte[]`),
  declare `public byte Q;` + `public int Im;`. e-1a's synthetic fixtures follow that shape.

### RECON FINDINGS that refine the scoped-plan prose (the code WINS — flagged)

> Discovered during write-time recon by reading the source. The implementer MUST re-confirm each at Task 0.

- **A1 — D2 INVERTS the storage for IX/IY (the key structural nuance).** Today IX/IY are bare 16-bit
  registers WITH backing fields (`Z80Spec.cs:42-43`, `CpuEmitter.cs:71`). The pair-view machinery
  (`CpuEmitter.cs:63-69`) makes a `HighHalf`/`LowHalf` register a COMPUTED property over its halves, with
  **NO backing field — the halves become the only storage**. So modeling IXh/IXl as half-views (D2) means:
  (a) declare `IXh`/`IXl`/`IYh`/`IYl` as 8-bit registers, (b) re-declare `IX`/`IY` as views
  (`highHalf: "IXh", lowHalf: "IXl"` / `highHalf: "IYh", lowHalf: "IYl"`), which **moves the storage from
  the `IX`/`IY` fields onto the new `IXh`/`IXl` fields**. This is transparent to every consumer (the runner
  uses `SetRegister("IX", …)`/`Check(…, "IX", …)`, which route through the computed property; the base/CB/
  ED ops never name IX), but it is a real change to the generated register layout. **Declaration ORDER
  matters:** the halves must be declared BEFORE the pair (the H/L-before-HL convention), because the
  emitter emits register declarations in list order and the pair view references the half fields. So
  `z80-semantics.json` lists `IXh, IXl, IYh, IYl` (8-bit) and then `IX, IY` (16-bit views). Confirm at
  Task 0 by reading `CpuEmitter.cs:61-72` + the existing AF/BC ordering.
- **A2 — `Indexed`'s byte-length is mode-dependent and the COMPOUND case is e-1b's, not `ModeLength`'s.**
  The DD/FD-CORE indexed forms (e.g. `DD 7E` = LD A,(IX+d)) are 3 bytes (prefix + opcode + displacement) —
  `ModeLength("Indexed") => 3` covers them. The DDCB/FDCB COMPOUND forms (`DD CB d op`) are 4 bytes with
  the displacement BEFORE the opcode — those do NOT take the `Indexed`-via-`ModeLength` path; e-1b's
  declarative compound decoder computes their length by consumption (`UnitsConsumed × UnitBytes`). So
  e-1a's `ModeLength("Indexed") => 3` is correct for the rows e-2 will add; the 4-byte compound rows are
  e-1b/e-3's concern. Record this so e-1a does NOT try to make `ModeLength` express 4 bytes.
- **A3 — the runner IX/IY set/check ALREADY EXISTS** (see Ground truth above). The scoped plan
  (`…-ixiy-prefixes.md:103-108`) flagged this as "may not yet — CONFIRM at Task 0 … likely small runner
  change." Recon resolves it: `Z80TomHarteRunner.cs:46,64` already do it. e-1a's Task 0 verifies; no change
  expected.
- **A4 — `Indexed` does NOT need a new `InstructionClass`.** It is an ADDRESSING mode, orthogonal to the
  op-class. The indexed `(IX+d)` ops (e-2) will reuse the existing Z80 classes (Z80Ld/Z80Alu/Z80Rot/etc.)
  with `mode == "Indexed"`; the per-class `ValidateModeForClass` arms must then ACCEPT `Indexed` — but that
  is an e-2 change (it depends on which classes carry indexed rows). e-1a only makes `Indexed` a *parseable*
  mode (the four mirror tables + ModeLength + JitMode); it does NOT widen any class's mode-legality (no row
  uses `Indexed` yet, so no `ValidateModeForClass` arm needs it). Confirm the parser does not REJECT an
  `Indexed`-mode synthetic row at the *mode-membership* check (`s_addrModes.Contains`) — that is the gate
  e-1a opens. A synthetic e-1a test uses a throwaway class (e.g. a trivial `Register`-class stub) carrying
  `AddrMode.Indexed` only to prove the mode parses + descriptors emit; the real class/mode pairing is e-2.

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Core/Specification/AddrMode.cs` | Modify | Add the `Indexed` enum member (after `Bit`). |
| `src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs` | Modify | Add the `Indexed` `JitMode` member (mirror; after `Bit`). |
| `src/CpuEmulator.Generators/SpecParser.cs` | Modify | Add `"Indexed"` to `s_addrModes`. |
| `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs` | Modify | Add `"Indexed"` to `SupportedModes` (flip the M3.4 enumerated finding); update the `:56-57` comment. |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | Add the `ModeLength("Indexed") => 3` arm; add the `EmitZ80IndexedEa` EA helper. |
| `tools/CpuEmulator.SpecImporter/data/z80-semantics.json` | Modify | Declare IXh/IXl/IYh/IYl (8-bit) BEFORE IX/IY; re-declare IX/IY as views (D2 / A1). |
| `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` | Modify (regenerated) | The regenerated spec — IXh/IXl/IYh/IYl present, IX/IY now views. |
| `tests/CpuEmulator.Tests/Generators/Z80IndexedModeTests.cs` | Create | The `Indexed` mode parses + descriptor emits `JitMode.Indexed` (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80IndexHalfViewTests.cs` | Create | IXh/IXl/IYh/IYl round-trip + IX/IY-as-view bidirectionality (synthetic). |
| `tests/CpuEmulator.Tests/Generators/Z80IndexedEaTests.cs` | Create | `EmitZ80IndexedEa` computes a signed `(IX+d)` EA (synthetic, via a probe op). |
| `tests/CpuEmulator.Tests/Importer/Z80IndexRegisterTests.cs` | Create | The importer emits IXh/IXl/IYh/IYl + IX/IY-view rows in the right order; the regen byte-shape. |

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502
> additivity guards + the whole Z80 staying green at the universal Q/WZ/IM bar), then commit. Tasks are
> dependency-ordered so the suite builds and stays green after every task. Literal code is given for every
> load-bearing piece. The synthetic-spec tests (via `GeneratorTestHost.CompileAndLoadType`) decouple from
> the real `Z80Spec.cs` regen, which lands atomically late (Task 4). Structured synthetic fixtures use
> `IAddressSpace _bus`, declare `public byte Q;` + `public int Im;` (M3.4d deviation #1).

### Task 0: Baseline + shipped-surface recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch.** Create the branch off the current main:
  Run: `git switch -c feat/m3-z80-ixiy-e1a`
  Expected: on the new branch, head at the M3.4e/M3.4d merge.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 0 failures, 0 unexpected skips. Record the EXACT count (the closeout pins it).
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean (no warnings).

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds:**
  - `src/CpuEmulator.Core/Specification/AddrMode.cs:7-22` (21 members, `Indexed` absent), the three mirrors:
    `SpecParser.cs:164-174` (`s_addrModes`), `SpecFileEmitter.cs:49-60` (`SupportedModes` + the `:56-57`
    "Indexed stays OUT" comment), `OpcodeDescriptor.cs:20-33` (`JitMode`, ends at `Bit`).
  - `CpuEmitter.cs:2815-2827` (`ModeLength` — the `_ => throw`; confirm there is NO `Indexed` arm),
    `:3208-3255` (`DescriptorLiteral`/`KeyedDescriptorLiteral` interpolating `JitMode.{insn.Mode}`),
    `:3310-3358` (`ClassifyForJit` — every Z80 class is a fallback; a new mode adds no class).
  - **The pair-view machinery + the storage-inversion (RECON-FINDING A1):** `CpuEmitter.cs:61-72` (a
    `HighHalf`/`LowHalf` register is a COMPUTED property, NO backing field; a plain register is a field),
    `:80-99` (`GetRegister`/`SetRegister` switch on NAME — works for both shapes). Confirm
    `z80-semantics.json:25-36` declares IX/IY as bare 16-bit (`:25-26`) and AF/BC/… as views over
    halves-declared-first (`:29-36`).
  - **The runner ALREADY wires IX/IY (RECON-FINDING A3):** `Z80TomHarteRunner.cs:46` (sets IX/IY), `:64`
    (checks IX/IY); `Z80TomHarteCase.cs:84` (parses ix/iy). **Confirm — no change needed.**
  - `tests/CpuEmulator.Tests/Generators/GeneratorTestHost.cs` (`CompileAndLoadType`/`Run` shapes); an
    existing structured synthetic fixture (e.g. `Z80EdIoTests.cs`/`Z80CbRotateTests.cs`) for the
    `IAddressSpace _bus` + `public byte Q; public int Im;` partial shape.

- [ ] **Step 4:** No commit (read-only). Proceed to Task 1.

---

### Task 1: The `Indexed` AddrMode + its four mirrors + `ModeLength` + `JitMode` (TDD)

> Add `Indexed` to the four mirror tables (`AddrMode`, `s_addrModes`, `SupportedModes`, `JitMode`) + the
> `ModeLength("Indexed") => 3` arm, so an `Indexed`-mode row PARSES, descriptors EMIT (`JitMode.Indexed`),
> and `ModeLength` does not throw. No opcode uses it yet — proven by a synthetic spec carrying one
> `Indexed`-mode probe row (RECON-FINDING A4: a throwaway class, the proof is mode-parseability).

**Files:**
- Modify: `src/CpuEmulator.Core/Specification/AddrMode.cs`
- Modify: `src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs`
- Modify: `src/CpuEmulator.Generators/SpecParser.cs`
- Modify: `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs`
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`ModeLength`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80IndexedModeTests.cs` (create)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/Generators/Z80IndexedModeTests.cs`. A synthetic spec carries ONE row in
  `AddrMode.Indexed` (a trivial `Register`-class op stub — the proof is that the mode parses + the
  descriptor carries `JitMode.Indexed`, NOT that an indexed body is emitted; per RECON-FINDING A4 the real
  op/class pairing is M3.4e-2). Assert: no generator ERROR diagnostics + the descriptor table text contains
  `JitMode.Indexed`.

```csharp
using Microsoft.CodeAnalysis;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedModeTests
{
    private const string Spec = """
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixm")]
        public static class IxmSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status), new("B", 8),
                new("C", 8), new("D", 8), new("E", 8), new("H", 8), new("L", 8),
                new("WZ", 16), new("IX", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("HL", 16, HighHalf: "H", LowHalf: "L"),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                // A probe row in Indexed mode (a register stub — proving the MODE parses + descriptors
                // emit JitMode.Indexed; the real (IX+d) op/class pairing is M3.4e-2).
                Insn(0xDD, 0x7E, "LD", AddrMode.Indexed, [Transfer("A", "A")]),
            ];
        }
        """;

    [Fact]
    public void Indexed_mode_parses_and_emits_JitMode_Indexed()
    {
        var result = GeneratorTestHost.Run(Spec);
        Assert.Empty(result.GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
        Assert.Contains("CpuEmulator.Core.Jit.JitMode.Indexed", result.GeneratedText);
    }
}
```

  > **Confirm the probe op/class.** `Transfer("A","A")` is the simplest existing flag-free op (read its
  > signature at Task 0); if it does not classify cleanly with `Indexed`, use whatever the codebase's
  > simplest zero-side-effect op is. The assertion that MATTERS is `JitMode.Indexed` in the descriptor
  > text + no generator error — NOT the body. If the chosen class's `ValidateModeForClass` REJECTS
  > `Indexed` (because no class yet allows it — RECON-FINDING A4), the test will show a CLASSIFY error; in
  > that case use a class whose `ValidateModeForClass` arm is permissive, or note that proving
  > mode-parseability requires the e-2 class-widening and instead assert only `s_addrModes.Contains` at the
  > parser-unit level. Read `ValidateModeForClass` at Task 0 and pick the cleanest probe; the goal is a
  > GREEN proof that `Indexed` is a first-class declarable mode.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedModeTests"`
  Expected: FAIL — `AddrMode.Indexed` does not exist (compile error in the synthetic spec) and/or
  `s_addrModes` rejects `"Indexed"`.

- [ ] **Step 3: Add `Indexed` to `AddrMode`.** In `src/CpuEmulator.Core/Specification/AddrMode.cs`, after
  the `Bit` member (`:21`):

```csharp
    Bit,               // CB-plane bit/rotate/shift (BIT/RES/SET n,r ; RLC/RR/SLA/… r). 2 bytes: 0xCB + op
    // M3.4e-1a (Z80 IX/IY): the indexed effective address (IX+d)/(IY+d). The DD/FD-core forms are 3
    // bytes (prefix + opcode + signed displacement); the DDCB/FDCB compound forms put the displacement
    // BEFORE the opcode (4 bytes) and decode via the compound-prefix walk (M3.4e-1b), not ModeLength.
    Indexed,
```

- [ ] **Step 4: Add `Indexed` to `JitMode`.** In `src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs`, after the
  `Bit` member (`:32`):

```csharp
    Bit,               // M3.4b (CB plane): BIT/RES/SET + rotate/shift. Z80 interpreter-only — a JIT fallback.
    Indexed,           // M3.4e-1a (Z80 IX/IY): (IX+d)/(IY+d). Z80 interpreter-only — a JIT fallback.
```

- [ ] **Step 5: Add `"Indexed"` to the two importer/parser mirrors.** In
  `src/CpuEmulator.Generators/SpecParser.cs` `s_addrModes` (`:172-173`):

```csharp
        "Register", "RegisterIndirect", "ImmediateExtended", "ExtendedAddress", "RelativeJump",
        "Bit",   // M3.4b (CB plane)
        "Indexed",   // M3.4e-1a (Z80 IX/IY): (IX+d)/(IY+d)
```

  In `tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs` `SupportedModes` (`:56-60`) — REPLACE the
  "Indexed stays OUT" comment + add the member:

```csharp
        // M3.4a (additive): the Z80 register-shape modes the base plane needs.
        "Register", "RegisterIndirect", "ImmediateExtended", "ExtendedAddress", "RelativeJump",
        "Bit",   // M3.4b (CB plane): BIT/RES/SET + rotate/shift
        // M3.4e-1a (additive): the indexed (IX+d)/(IY+d) mode. The DD/FD-core indexed rows emit as
        // Indexed; the DDCB/FDCB compound rows decode via the compound-prefix walk (M3.4e-1b).
        "Indexed",
```

- [ ] **Step 6: Add the `ModeLength("Indexed")` arm.** In `src/CpuEmulator.Generators/CpuEmitter.cs`
  `ModeLength` (`:2815-2827`), add `Indexed` to the 3-byte group (prefix + opcode + displacement — the
  DD/FD-core forms; the 4-byte DDCB compound is e-1b's decode-walk concern, RECON-FINDING A2):

```csharp
        "Absolute" or "AbsoluteX" or "AbsoluteY" or "Indirect" => 3,
        "ImmediateExtended" or "ExtendedAddress" => 3,          // M3.4a: opcode + 16-bit operand
        "Indexed" => 3,                                         // M3.4e-1a: prefix + opcode + displacement
```

- [ ] **Step 7: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedModeTests"`
  Expected: PASS.

- [ ] **Step 8: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — the
  6502 names no `Indexed` row; adding an enum member it never uses cannot change its `.g.cs`).

  > **6502 guard note:** `Indexed` is a new enum member only. The 6502 spec declares no `Indexed` row, so
  > its descriptor table + bodies are unchanged. The `RegeneratedSpecTests` byte-identity guard confirms.

- [ ] **Step 9: Commit.**

```bash
git add src/CpuEmulator.Core/Specification/AddrMode.cs src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs \
        src/CpuEmulator.Generators/SpecParser.cs tools/CpuEmulator.SpecImporter/SpecFileEmitter.cs \
        src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/Z80IndexedModeTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): add the Indexed AddrMode + JitMode.Indexed mirror + ModeLength arm (no opcode live)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1.

---

### Task 2: The `(IX+d)`/`(IY+d)` effective-address emit helper (TDD)

> Add `EmitZ80IndexedEa(StringBuilder sb, string indexReg)` — a small static (the `EmitWz`/`Z80IndirectPair`
> shape) that emits the signed effective-address computation into a known local. Proven synthetically by a
> probe op that reads the EA-targeted byte (no real DD/FD opcode — the helper is exercised through a tiny
> hand-wired probe arm guarded so it does not affect any real row).

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs` (`EmitZ80IndexedEa` helper)
- Test: `tests/CpuEmulator.Tests/Generators/Z80IndexedEaTests.cs` (create)

> **Design decision (recorded):** the EA helper is a PURE emitter helper — it appends one C# statement and
> takes the index-register name + the displacement-source expression as parameters, so M3.4e-2/3's indexed
> arms call it uniformly. The displacement byte's RUNTIME source differs by form (DD/FD-core: the byte AFTER
> the opcode, read by the decode walk into the operand `lo`; DDCB compound: the byte BEFORE the opcode,
> surfaced by e-1b's compound walk). e-1a's helper takes the displacement EXPRESSION as a parameter
> (`dispExpr`) so it is agnostic to where the byte came from; e-1b/e-2 pass the right source. e-1a proves
> the helper with a literal `(sbyte)` displacement local the probe sets up.

- [ ] **Step 1: Write the failing test.** Create `tests/CpuEmulator.Tests/Generators/Z80IndexedEaTests.cs`.
  Because no real `Indexed` body is emitted in e-1a, the cleanest synthetic proof targets the HELPER's
  output text directly via a generator-unit probe OR via a tiny synthetic op whose body the test author
  wires to call `EmitZ80IndexedEa`. The PRAGMATIC e-1a approach (avoids a throwaway emit arm): assert the
  helper's emitted STRING shape through a focused generator-text check — a synthetic spec with an
  `Indexed`-mode probe row whose emitted body (once e-2 adds the arm) would call the helper. **Since e-1a
  emits no indexed body, prove the helper as a STATIC-METHOD unit test** (the helper is `internal static`,
  reachable from the test assembly via `InternalsVisibleTo`, which the generator project already grants the
  test project — CONFIRM at Task 0):

```csharp
using System.Text;
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexedEaTests
{
    [Fact]
    public void EmitZ80IndexedEa_emits_signed_effective_address_into_a_local()
    {
        var sb = new StringBuilder();
        // The helper appends one statement computing the signed EA into __ea from the index register
        // and a displacement expression. (lo) is the decode walk's first operand byte (the DD/FD-core
        // displacement); e-1b/e-2 pass the right source for the compound form.
        CpuEmulator.Generators.CpuEmitter.EmitZ80IndexedEa(sb, "IX", "lo");
        string s = sb.ToString();
        Assert.Contains("ushort __ea", s);
        Assert.Contains("(sbyte)", s);    // SIGNED displacement — the (IX-128..+127) range
        Assert.Contains("IX", s);
    }
}
```

  > **If `EmitZ80IndexedEa` cannot be made test-visible** (the generator's `InternalsVisibleTo` does not
  > reach the test project, or the helper must stay `private`), fall back to the synthetic-spec proof:
  > keep the helper `private`, add a TEMPORARY synthetic probe op-class arm that calls it (gated to the
  > synthetic spec), and assert the GENERATED text contains `ushort __ea` + `(sbyte)`. CONFIRM the
  > generator's existing test-visibility posture at Task 0 (read how other `EmitZ80*` helpers are unit-
  > tested — if they are only proven via synthetic specs, follow that; if a helper is directly unit-tested,
  > mirror it). The load-bearing assertion is `(sbyte)` (signed displacement) regardless of the vehicle.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedEaTests"`
  Expected: FAIL — `EmitZ80IndexedEa` does not exist.

- [ ] **Step 3: Add the `EmitZ80IndexedEa` helper.** In `src/CpuEmulator.Generators/CpuEmitter.cs`, near
  `EmitWz`/`Z80IndirectPair`:

```csharp
    /// <summary>M3.4e-1a (Z80 IX/IY): emit the indexed effective-address computation
    /// <c>ushort __ea = unchecked((ushort)(&lt;indexReg&gt; + (sbyte)(&lt;dispExpr&gt;)));</c>. The
    /// displacement is SIGNED (the (IX+d) range is IX-128..IX+127). <paramref name="indexReg"/> is the
    /// 16-bit index register NAME ("IX"/"IY"); <paramref name="dispExpr"/> is the displacement-byte
    /// expression — for the DD/FD-core forms the decode walk's first operand byte (the byte after the
    /// opcode); for the DDCB/FDCB compound forms the byte the compound walk surfaces (M3.4e-1b). Every
    /// indexed emit arm (M3.4e-2/3) calls this so the EA is computed identically and lives in a known
    /// local (__ea). e-1a ships the helper; no live opcode calls it yet.</summary>
    internal static void EmitZ80IndexedEa(StringBuilder sb, string indexReg, string dispExpr) =>
        sb.AppendLine($"        ushort __ea = unchecked((ushort)({indexReg} + (sbyte)({dispExpr})));");
```

  > **Visibility:** `internal static` so the test assembly can unit-test it directly (the generator project
  > grants `InternalsVisibleTo` the test project — CONFIRM at Task 0; if not, either add the attribute in
  > the generator project's csproj as a one-line additive change, or keep the helper `private` and use the
  > synthetic-spec fallback from Step 1). Match the surrounding helpers' accessibility convention.

- [ ] **Step 4: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexedEaTests"`
  Expected: PASS.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (the helper is UNCALLED by
  any real row — adding an unused emitter helper cannot change any generated `.g.cs`).

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs \
        tests/CpuEmulator.Tests/Generators/Z80IndexedEaTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): add the EmitZ80IndexedEa (IX+d)/(IY+d) signed-EA emit helper (uncalled; M3.4e-2 uses it)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1.

---

### Task 3: IXh/IXl/IYh/IYl half-views — the D2 storage inversion (TDD, synthetic)

> Prove the half-view register shape works BEFORE touching the real `z80-semantics.json`/regen: a synthetic
> spec declaring IXh/IXl (8-bit) + IX as a view, asserting bidirectional round-trip (write IXh → IX
> reflects it; write IX → IXh/IXl reflect it). This validates the D2 model (RECON-FINDING A1: storage moves
> to the halves) against the EXISTING pair-view machinery — no new generator code, just the proof the
> shape behaves. The real declaration + regen is Task 4.

**Files:**
- Test: `tests/CpuEmulator.Tests/Generators/Z80IndexHalfViewTests.cs` (create)
  (No generator change — the pair-view machinery already supports this shape; this task PROVES it for the
  IX/IY case before Task 4 commits to the real spec.)

- [ ] **Step 1: Write the test.** Create `tests/CpuEmulator.Tests/Generators/Z80IndexHalfViewTests.cs`. A
  synthetic spec declaring `IXh`/`IXl` (8-bit) BEFORE `IX` (a 16-bit view over them), mirroring the
  H/L→HL ordering. Assert bidirectionality through `GetRegister`/`SetRegister`.

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80IndexHalfViewTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("ixh")]
        public static class IxhSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("IXh", 8), new("IXl", 8),   // halves FIRST (the H/L-before-HL convention, A1)
                new("WZ", 16),
                new("SP", 16, RegisterRole.StackPointer), new("PC", 16, RegisterRole.ProgramCounter),
                new("IX", 16, HighHalf: "IXh", LowHalf: "IXl"),   // IX is now a VIEW (storage = halves)
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [new PrefixByte(0xDD)], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0x00, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class IxhCpu
        {
            private readonly IAddressSpace _bus;
            public byte Q;
            public int Im;
            public IxhCpu(IAddressSpace bus) { _bus = bus; }
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

    private static (object Cpu, System.Type T) Build()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.IxhCpu");
        var bus = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        bus.MapMemory(0x0000, new byte[0x10000], writable: true);   // M3.4d: map the synthetic bus
        var cpu = System.Activator.CreateInstance(t, new object[] { bus })!;
        return (cpu, t);
    }
    private static void Set(object cpu, System.Type t, string r, ulong v) =>
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { r, v });
    private static ulong Get(object cpu, System.Type t, string r) =>
        (ulong)t.GetMethod("GetRegister")!.Invoke(cpu, new object[] { r })!;

    [Fact]
    public void Writing_IX_reflects_into_its_halves()
    {
        var (cpu, t) = Build();
        Set(cpu, t, "IX", 0xABCD);
        Assert.Equal(0xABu, (uint)Get(cpu, t, "IXh"));   // high half
        Assert.Equal(0xCDu, (uint)Get(cpu, t, "IXl"));   // low half
    }

    [Fact]
    public void Writing_a_half_reflects_into_IX()
    {
        var (cpu, t) = Build();
        Set(cpu, t, "IX", 0x0000);
        Set(cpu, t, "IXh", 0x12);
        Set(cpu, t, "IXl", 0x34);
        Assert.Equal(0x1234u, (uint)Get(cpu, t, "IX"));   // the pair view reflects both halves
    }
}
```

- [ ] **Step 2: Run the test.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexHalfViewTests"`
  Expected: PASS immediately — the pair-view machinery (`CpuEmitter.cs:63-69`) already supports this shape.
  (If it FAILS, the failure is informative — it means the IX-as-view emit has a gap the real Task 4 would
  also hit; debug it HERE in the synthetic before touching the real spec.)

  > **Why this task has no implementation step:** D2 reuses the EXISTING pair-view machinery; there is no
  > generator code to add. This task is a GREEN-on-arrival proof that the IX/IY half-view shape behaves
  > under the current emitter, de-risking the real Task 4 regen. If green, the storage-inversion (A1) is
  > sound; proceed to Task 4.

- [ ] **Step 3: Full gate + commit.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.

```bash
git add tests/CpuEmulator.Tests/Generators/Z80IndexHalfViewTests.cs
git commit -m "$(cat <<'EOF'
test(z80): prove the IXh/IXl half-view shape (D2 storage inversion) on the existing pair-view machinery

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 4: Declare IXh/IXl/IYh/IYl + the IX/IY views in `z80-semantics.json`; regen; re-green (TDD + gate)

> Apply D2 to the REAL spec: declare `IXh`/`IXl`/`IYh`/`IYl` (8-bit) before `IX`/`IY` (now views) in
> `z80-semantics.json`, regenerate `Z80Spec.cs`, and re-run the WHOLE Z80 UAT (the storage inversion must
> not regress any base/CB/ED case — those carry init.ix == final.ix, so IX/IY round-tripping through the
> new view path must stay identity). The importer test gates the emitted register order/shape.

**Files:**
- Modify: `tools/CpuEmulator.SpecImporter/data/z80-semantics.json`
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Spec.cs` (regenerated)
- Test: `tests/CpuEmulator.Tests/Importer/Z80IndexRegisterTests.cs` (create)

- [ ] **Step 1: Write the failing importer test.** Create
  `tests/CpuEmulator.Tests/Importer/Z80IndexRegisterTests.cs` asserting the importer emits the IX/IY
  half-views with the halves BEFORE the pair (the H/L-before-HL order), so the generated pair-view property
  compiles. Mirror the existing importer-register tests' shape (read an existing
  `tools/CpuEmulator.SpecImporter` register test at Task 0 for the exact harness). The assertion targets
  the EMITTED `Z80Spec.cs` register-declaration text:

```csharp
using Xunit;

namespace CpuEmulator.Tests.Importer;

public class Z80IndexRegisterTests
{
    [Fact]
    public void Ix_iy_emit_as_views_over_8bit_halves_declared_first()
    {
        // Regenerate (or load the committed) Z80Spec.cs register block and assert the half-view shape.
        // Use the same importer-invocation harness the existing importer tests use (Task 0 confirms it).
        string spec = Z80ImporterTestHarness.RegenerateZ80SpecText();   // or read the committed file

        // The 8-bit halves are declared (storage); IX/IY are views over them (D2 / A1).
        Assert.Contains("new(\"IXh\", 8)", spec);
        Assert.Contains("new(\"IXl\", 8)", spec);
        Assert.Contains("new(\"IYh\", 8)", spec);
        Assert.Contains("new(\"IYl\", 8)", spec);
        Assert.Contains("new(\"IX\", 16, HighHalf: \"IXh\", LowHalf: \"IXl\")", spec);
        Assert.Contains("new(\"IY\", 16, HighHalf: \"IYh\", LowHalf: \"IYl\")", spec);

        // Ordering: each half is declared BEFORE its pair (the pair view references the half fields).
        Assert.True(spec.IndexOf("\"IXh\"") < spec.IndexOf("HighHalf: \"IXh\""),
            "IXh must be declared before the IX view that references it");
    }
}
```

  > **Confirm the harness.** Read how the existing importer tests regenerate/inspect `Z80Spec.cs` (Task 0):
  > whether they call the importer in-process and inspect the emitted string, or read the committed file.
  > Use the SAME mechanism; the `Z80ImporterTestHarness.RegenerateZ80SpecText()` above is illustrative —
  > substitute the project's actual harness call. If the importer tests only assert on the committed file,
  > this test reads the committed (Task-4-regenerated) `Z80Spec.cs`.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexRegisterTests"`
  Expected: FAIL — IXh/IXl/IYh/IYl are not yet declared; IX/IY are still bare 16-bit.

- [ ] **Step 3: Edit `z80-semantics.json`.** REPLACE the bare IX/IY declarations (`:25-26`) and move them
  to the pair-view block, declaring the halves FIRST (RECON-FINDING A1). The 8-bit halves go with the
  other 8-bit registers (near A/F/B/C…); the IX/IY views go with the AF/BC/DE/HL pair-view block:

```json
    { "name": "IXh", "bits": 8 },
    { "name": "IXl", "bits": 8 },
    { "name": "IYh", "bits": 8 },
    { "name": "IYl", "bits": 8 },
```
  (placed among the 8-bit registers, BEFORE the 16-bit pair-view block) and, in the pair-view block
  alongside AF/BC/DE/HL:

```json
    { "name": "IX", "bits": 16, "highHalf": "IXh", "lowHalf": "IXl" },
    { "name": "IY", "bits": 16, "highHalf": "IYh", "lowHalf": "IYl" },
```
  REMOVE the old `{ "name": "IX", "bits": 16 }` / `{ "name": "IY", "bits": 16 }` lines (`:25-26`).

  > **Preserve the runner-visible NAME "IX"/"IY".** The runner uses `SetRegister("IX", …)`/`Check(…,
  > "IX", …)` — that NAME survives (IX is still a register, now a view). The change is the STORAGE
  > (halves), invisible to the runner. The base/CB/ED vectors carry init.ix == final.ix, so identity
  > round-trip through the view is what Step 5 verifies.

- [ ] **Step 4: Regenerate `Z80Spec.cs`.**
  Run:
```bash
dotnet run --project tools/CpuEmulator.SpecImporter -- \
  --dataset tools/CpuEmulator.SpecImporter/data/z80-opcodes.json \
  --semantics tools/CpuEmulator.SpecImporter/data/z80-semantics.json \
  --out src/CpuEmulator.Cpus.Z80/Z80Spec.cs
```
  Expected diff: the `Registers` block gains `IXh`/`IXl`/`IYh`/`IYl` (8-bit) and IX/IY flip to views; NO
  `Insn`-row change (no dataset row added). Review the diff to confirm ONLY the register block changed.

- [ ] **Step 5: Re-green the WHOLE Z80 UAT (the e-1a exit criterion).**
  Run the staged gate over base + CB + ED:
```bash
CPUEMULATOR_Z80_REGS_ONLY=1 dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
```
  Expected: base + CB + ED-core + ED-block **0 failures** — the IX/IY storage inversion is identity for
  every existing op (none names IX/IY; the runner round-trips IX/IY through the new view path and the
  vectors' init.ix == final.ix). Any failure here means the view round-trip is not identity — apply
  `superpowers:systematic-debugging`, but recon (Task 3 green) makes a failure unlikely.

- [ ] **Step 6: Confirm the regression bar + commit.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80IndexRegisterTests"` → PASS.
  Run: `dotnet test` → full suite green (record the count).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — the
  semantics change is Z80-only).

```bash
git add tools/CpuEmulator.SpecImporter/data/z80-semantics.json src/CpuEmulator.Cpus.Z80/Z80Spec.cs \
        tests/CpuEmulator.Tests/Importer/Z80IndexRegisterTests.cs \
        docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1a-addrmode-ea.md
git commit -m "$(cat <<'EOF'
feat(z80): model IXh/IXl/IYh/IYl as half-views of IX/IY (D2); regen; whole-Z80 UAT re-green

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

  > **The doc add to this commit** carries the filled closeout below. (Per the project's docs-per-PR
  > convention, the plan doc travels with its slice's final commit.)

**New-test estimate:** ~1.

---

### Task 5: PR

- [ ] **Step 1: Push + open the PR.**
  Run: `git push -u origin feat/m3-z80-ixiy-e1a` (after the user approves; merge via PR per CLAUDE.md).
  Open a PR targeting `main`. The PR body claims EXACTLY: `Indexed` is a declarable AddrMode with its
  `JitMode.Indexed` mirror + `ModeLength` arm; the `(IX+d)`/`(IY+d)` signed-EA emit helper exists
  (uncalled); IXh/IXl/IYh/IYl are 8-bit half-views of IX/IY (storage moved to the halves; IX/IY now
  computed views); the whole Z80 (base + CB + ED + block) re-validated at the universal Q/WZ/IM bar;
  6502 byte-identical. Name what is STILL deferred: the compound-prefix DECODER = M3.4e-1b (the next slice);
  any DD/FD opcode going live = M3.4e-2/3; interrupt servicing + ZEXALL + the JIT = M3.5. NEVER overstate —
  **no DD/FD vector is green here.** Include a **Docs Impact** section linking the overview + the scoped
  plan + the e-1b plan.

---

## Plan self-review (completed at write time)

- **Scope coverage (the 3 IN-scope items):**
  - **(1) the `Indexed` AddrMode + 4 mirrors + ModeLength + JitMode** — Task 1. ✓
  - **(2) IXh/IXl/IYh/IYl half-views (D2)** — Task 3 (synthetic proof) + Task 4 (real declare + regen). ✓
  - **(3) the `(IX+d)`/`(IY+d)` EA helper** — Task 2. ✓
- **OUT-of-scope honored:** NO `DecodeStructure.cs`/decode-walk change (that is e-1b); NO dataset row added;
  NO `dd *.json` vector asserted green; NO `ValidateModeForClass` widening (no row uses `Indexed` yet —
  RECON-FINDING A4). ✓
- **Placeholder scan:** every code step shows literal code; no `// TODO` in emitted code (the EA helper is
  uncalled, not a stub); no "TBD"/"similar to Task N". ✓
- **Type/name consistency:** `Indexed` (AddrMode + JitMode + s_addrModes + SupportedModes + ModeLength);
  `EmitZ80IndexedEa(sb, indexReg, dispExpr)` (Task 2 signature matches the Task 2 test call shape — note
  the test's 2-arg call vs the 3-arg helper: RECONCILE at implementation — the test must pass the third
  `dispExpr` arg, or the helper defaults it; the literal-code helper is 3-arg, so the Task 2 test snippet's
  2-arg call is updated to `EmitZ80IndexedEa(sb, "IX", "lo")` → it already passes `"lo"` as the 2nd arg
  with NO 3rd; the implementer makes the test call match the final signature — flagged as the one
  reconcile). `IXh`/`IXl`/`IYh`/`IYl` (semantics JSON + Z80Spec + the half-view test + the importer test). ✓
- **Code/recon contradictions surfaced (the code wins):** (A1) D2 inverts storage — halves-first ordering;
  (A2) `ModeLength("Indexed")=3` is the DD/FD-core case, the 4-byte compound is e-1b's; (A3) the runner
  ALREADY wires IX/IY — no runner change; (A4) `Indexed` needs no new class, no `ValidateModeForClass`
  widening in e-1a. ✓
- **Build-green-after-every-task:** Tasks 1–3 are synthetic/helper-only (decoupled from the regen); the
  regen + whole-Z80 re-green is Task 4 (the only TomHarte-affecting task). ✓
- **One reconcile flagged:** the Task 2 test's helper-call arity must match the final `EmitZ80IndexedEa`
  signature (3-arg: `sb, indexReg, dispExpr`). The implementer aligns the test call when writing Task 2.

## Closeout (filled at completion)

| Commit | Content | Suite |
|---|---|---|
| d93d399 (Task 1) | Indexed AddrMode + JitMode.Indexed + ModeLength arm | green (2309) |
| 4c8484c (Task 2) | EmitZ80IndexedEa signed-EA helper (uncalled) | green (2311) |
| f13ba4f (Task 3) | IXh/IXl/IYh/IYl half-view shape proof (synthetic) | green (2315) |
| (Task 4) | IXh/IXl/IYh/IYl declared; IX/IY views; regen; whole-Z80 re-green | green (2317) |

| Closeout metric | Value |
|---|---|
| Baseline test count (Task 0) | 2306 (0 failed, 0 skipped) |
| Final test count | 2317 (0 failed, 0 skipped) — +11 (1 existing register-count test updated, not added) |
| `Indexed` mode declarable? | YES — AddrMode + JitMode + s_addrModes + SupportedModes + ModeLength(=3) |
| IXh/IXl/IYh/IYl present? | YES — 8-bit half-views; IX/IY now computed pair-views (storage = halves) |
| `(IX+d)` EA helper present? | YES — `EmitZ80IndexedEa`; UNCALLED (M3.4e-2 wires it) |
| Any DD/FD opcode live? | NO — e-1a is framework-only; no `dd *.json` asserted green |
| Whole-Z80 UAT (full) | base + CB + ED re-green — `CPUEMULATOR_UAT=full` 588/0/0; regs-only (588/0/0) + standard sample (588/0/0) tiers also 0 failures, final Q/WZ/IM on every case. The D2 storage inversion is transparent. |
| Runner IX/IY change needed? | NO — already set/checked (RECON-FINDING A3) |
| 6502 un-regressed? | YES — RegeneratedSpecTests byte-identity green |
| Any 6502 file changed? | NONE (additive) |
| `-warnaserror` | clean |
| Still deferred | the compound decoder (M3.4e-1b); DD/FD opcodes live (M3.4e-2/3); servicing + ZEXALL + JIT (M3.5) |
| Recommended next chunk | M3.4e-1b — the compound-prefix decoder |

### Deviations from the plan (honest record)

- **Task 1 proof vehicle.** The plan's Step-1 snippet ran a synthetic `Indexed`-mode probe row through
  `GeneratorTestHost.Run` and asserted `JitMode.Indexed` in the descriptor text. In practice no op-class
  accepts `Indexed` yet (RECON-FINDING A4 — the class-widening is M3.4e-2), so a live `Indexed`-mode row is
  (correctly) rejected at the class/mode legality check (CPUGEN010) and never reaches descriptor emission.
  Following the plan's stated fallback AND the M3.4b precedent (`Z80CbModeTests` proved the `Bit` mode by
  enum-membership, not a live row), Task 1's test asserts: (a) `AddrMode.Indexed`/`JitMode.Indexed`
  enum-membership, and (b) an `Indexed`-mode row PARSES past the `s_addrModes.Contains` gate (no "unknown
  AddrMode member" diagnostic — the membership gate e-1a opens) and is then rejected ONLY at the class/mode
  check. This is the precise, honest statement of e-1a's scope: `Indexed` is a declarable mode; no opcode is
  live. `ModeLength("Indexed")=3` and the `SupportedModes` mirror are proven structurally by the whole-suite
  green gate (the importer emits `JitMode.{Mode}` literally; a mismatched mirror would not compile).
- **Task 3 fixture shape.** The plan's synthetic fixture declared a `DecodeStructure` with a `0xDD` prefix
  but no prefixed `Insn` row, which trips CPUGEN012 ("prefix 0xDD has no prefixed Insn row") → `model.Decode`
  drops → the structured partials are not emitted → the hand-written partials cascade CS-errors. Debugged in
  the synthetic exactly as the plan intended (de-risking Task 4); the fix was to use the degenerate
  (no-`DecodeStructure`) register-file fixture shape from `RegisterPairAliasingTests` — a pure register-file
  half-view proof needs no structured decoder. The IX/IY round-trip + view-property assertions are unchanged
  (extended to cover IY as well as IX). The real spec (Task 4) carries the prefixed rows, so its regen is
  validated by the whole-Z80 re-green, not this synthetic.
- **Existing test updated (not added).** `SemanticsMapTests.Z80_registers_load_as_declared` asserted the Z80
  register count (31). The D2 storage inversion adds 4 half-views (IXh/IXl/IYh/IYl), so the count is now 35;
  the test's count, taxonomy comment, and IX/IY half-view assertions were updated to match. This is a
  legitimate consequence of the slice, not a regression.

## Slice docs index

- **Overview / sequencing:** `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md`
- **Scoped parent plan (the M3.4e outline):** `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md`
- **The other half of M3.4e-1 (the compound decoder):**
  `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-e1b-compound-decoder.md`
- **Depth templates + close-state records:** `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`,
  `…-ed-block-ops.md`
- **Architecture (Decisions 1, 3, 4, 7):** `docs/architecture/0001-z80-second-architecture.md`
