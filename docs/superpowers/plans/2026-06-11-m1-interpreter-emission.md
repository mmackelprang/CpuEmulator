# M1 Chunk 2b: Per-Opcode Interpreter Emission — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the 11-opcode 6502 subset execute with cycle-exact bus behavior — per-opcode bodies emitted by the generator from the parsed micro-op models — pinned by literal cycle-by-cycle bus-trace tests, plus the generated disassembler table and the recorded generator-hygiene carry-forwards. PR #3 of Milestone 1 (stacked on `feat/m1-generator-frontend`, PR #2).

**Architecture:** `CpuEmitter` grows per-opcode private methods composed from addressing-mode cycle templates (the spec-§5 "modes are micro-op templates" idea): every bus access goes through the hand-written `ReadBus`/`WriteBus` (each charging one cycle), dummy reads included, so the interpreter is cycle-true by construction. A `TracingAddressSpace` decorator records every access; tests assert literal `(address, value, read/write)` sequences. A parser validation pass (CPUGEN010) rejects mode/op combinations the emitter does not define.

**Tech Stack:** unchanged (net10.0, Roslyn 4.12.0, xUnit).

**Plan series:** 1 ✅ (PR #1, merged) · 2a ✅ (PR #2, merged) · **2b: this plan** · 3a importer · 3b full 6502 + TomHarte · 4 peripherals + host.

> **Stacking update (recorded at Task 8):** PR #2 (`feat/m1-generator-frontend`) merged while
> this plan was in flight, so PR #3's base is `main` (merge-base is PR #2's tip, `b2a7fc3`;
> the merge is clean) — not the stacked base the header and Task 8 originally named.

**Recorded deviations this plan makes deliberately:**
- **Dispatch stays a `switch`** (compiles to a jump table) rather than spec-§6's `delegate*[256]`. Zero-alloc and AOT-safe either way; the function-pointer table forces static per-opcode methods with an explicit `this` parameter for no measured benefit at 11 (or 151) opcodes. Revisit gate: M2 benchmarks (BenchmarkDotNet lands there anyway).
- **Generated state stays fields-on-class** (vs spec-§5 "state struct"). Partials make fields the natural seam for the hand-written half; the M2 JIT can hoist fields exactly as it would struct members. Recorded here as the standing decision; revisit only if M2's block compiler wants a flat snapshot type.

**Carry-forwards landed here** (from the 2a plan's amendment blockquotes): equatable `DiagnosticInfo` pipeline (the pre-`Collect` tripwire) + CPU-name-collision detection via `Collect()`; record-declaration rejection; `_cycles` → `private`; `WriteBus` added to the generated seam-contract header; mnemonic validation; `Flag`-member whitelist; trace-cycle convention documented.

---

## File structure

```
src/CpuEmulator.Generators/
    DiagnosticInfo.cs                    — NEW: equatable, location-bearing diagnostic carrier
    SpecDiagnostics.cs                   — MODIFY: + CPUGEN010 (and CPUGEN009 collision use)
    SpecParser.cs                        — MODIFY: emit DiagnosticInfo; mode/op validation;
                                            mnemonic + Flag whitelists
    SpecModel.cs                         — MODIFY: ParsedSpec carries DiagnosticInfo
    CpuSpecGenerator.cs                  — MODIFY: Collect() + collision check + materialize
    CpuEmitter.cs                        — MODIFY: per-opcode bodies, disassembler, header,
                                            private _cycles
src/CpuEmulator.Cpus.Mos6502/Mos6502Cpu.cs — unchanged (WriteBus goes live)
tests/CpuEmulator.Tests/
    Generators/PipelineHygieneTests.cs   — NEW: collision, record-decl, DiagnosticInfo equality
    Generators/ModeOpValidationTests.cs  — NEW: CPUGEN010 + whitelist diagnostics
    Generators/DisassemblerEmissionTests.cs — NEW
    Mos6502/TracingAddressSpace.cs       — NEW: recording bus decorator (+ BusAccess record)
    Mos6502/Mos6502TraceTests.cs         — NEW: cycle-exact per-opcode traces
    Mos6502/Mos6502ProgramTests.cs       — NEW: multi-instruction integration (CPU + Machine)
```

## The addressing-mode cycle templates (ground truth)

Cycle 1 is always the opcode fetch (in `Step`, via `ReadBus(PC)` then PC increment). Then:

| Mode | Class | Cycles after fetch | Bus pattern |
|---|---|---|---|
| Implied | register ops | 1 | dummy read at PC (no increment) |
| Immediate | Load-first | 1 | read operand at PC, PC++ |
| ZeroPage | read (Load…) | 2 | fetch zp addr; read data at addr |
| ZeroPage | write (Store) | 2 | fetch zp addr; write reg at addr |
| Absolute | read (Load…) | 3 | fetch lo; fetch hi; read data at EA |
| Absolute | write (Store) | 3 | fetch lo; fetch hi; write reg at EA |
| Absolute | Jump | 2 | fetch lo; fetch hi; PC = EA (no data access) |
| Relative | BranchIf | 1 / 2 / 3 | fetch offset; if taken: dummy read at PC; if page crossed: dummy read at (old page \| new lo); PC = target |

Instruction classes (validated by CPUGEN010, emitted by the templates):
- **register class** (Implied only): ops drawn from Transfer/Increment/SetNZ, in order.
- **load class** (Immediate/ZeroPage/Absolute): first op `Load(target)`, then register ops (SetNZ…). Immediate's data is the operand byte; ZP/Abs read data at EA.
- **store class** (ZeroPage/Absolute): exactly one `Store(source)`.
- **jump class** (Absolute): exactly one `Jump()`.
- **branch class** (Relative): exactly one `BranchIf(flag, when)`.

`SetNZ(s)` semantics: `P = (P & 0x7D) | (s == 0 ? 0x02 : 0x00) | (s & 0x80)` (Z is bit 1, N is bit 7 — matching the `Flag` enum's hardware values).

Branch emission (the only data-dependent timing in the subset — page-cross fix-up reads at the *wrong* address, exactly like silicon):

```csharp
byte offset = ReadBus(PC);
PC = unchecked((ushort)(PC + 1));
if (<taken-condition>)
{
    _ = ReadBus(PC);
    ushort target = unchecked((ushort)(PC + (sbyte)offset));
    if ((target & 0xFF00) != (PC & 0xFF00))
        _ = ReadBus((uint)((PC & 0xFF00) | (target & 0x00FF)));
    PC = target;
}
```

Taken-condition for `BranchIf(flag, when)`: `((P >> <bit>) & 1) == <when ? 1 : 0>` with `<bit>` from the Flag enum's hardware value, via an emitter-side name→bit map mirroring the enum (sync hazard recorded, same class as `s_regMembers`).

---

### Task 1: Branch + pipeline hygiene (DiagnosticInfo, Collect, collision, record-decl)

**Files:**
- Create: `src/CpuEmulator.Generators/DiagnosticInfo.cs`
- Modify: `SpecModel.cs`, `SpecParser.cs`, `CpuSpecGenerator.cs`, `SpecDiagnostics.cs` (no new IDs here; CPUGEN009 reused for collision/record-decl)
- Create: `tests/CpuEmulator.Tests/Generators/PipelineHygieneTests.cs`

- [ ] **Step 1: Branch** — `git checkout feat/m1-interpreter-emission` (already created from `feat/m1-generator-frontend`; verify with `git branch --show-current`).

- [ ] **Step 2: Write the failing tests**

`tests/CpuEmulator.Tests/Generators/PipelineHygieneTests.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CpuEmulator.Tests.Generators;

public class PipelineHygieneTests
{
    [Fact]
    public void Two_specs_colliding_on_namespace_and_cpu_name_report_CPUGEN009_not_a_crash()
    {
        // Same namespace, both derive "FooCpu" — previously crashed AddSource with CS8785.
        string source = GeneratorHappyPathTests.ValidSpecSource
            + """

            namespace TestCpu;

            [CpuSpecification("other", CpuName = "Tiny6502Cpu")]
            public static class OtherSpec
            {
                public static readonly RegisterDef[] Registers =
                [
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];

                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xEA, "NOP", AddrMode.Implied, []),
                ];
            }
            """;

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN009");
        Assert.DoesNotContain(result.CompilationDiagnostics, d => d.Id == "CS8785");
        Assert.Empty(result.GeneratedTrees); // neither emitted on collision
    }

    [Fact]
    public void Record_spec_class_reports_CPUGEN009()
    {
        string source = GeneratorHappyPathTests.ValidSpecSource
            .Replace("public static class Tiny6502Spec", "public record Tiny6502Spec");

        var result = GeneratorTestHost.Run(source);

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN009");
    }

    [Fact]
    public void Diagnostics_still_carry_precise_locations_after_the_DiagnosticInfo_conversion()
    {
        // Duplicate-opcode location must still point at the duplicate row (regression
        // guard: the DiagnosticInfo round-trip must preserve file path + span).
        string source = GeneratorHappyPathTests.ValidSpecSource.Replace(
            "Insn(0xEA, \"NOP\", AddrMode.Implied, []),",
            "Insn(0xA9, \"NOP\", AddrMode.Implied, []),");

        var result = GeneratorTestHost.Run(source);

        var diagnostic = Assert.Single(result.GeneratorDiagnostics, d => d.Id == "CPUGEN005");
        string locationText = diagnostic.Location.SourceTree!
            .GetText().ToString(diagnostic.Location.SourceSpan);
        Assert.StartsWith("Insn(0xA9, \"NOP\"", locationText);
    }
}
```

NOTE: after the DiagnosticInfo conversion the reported `Diagnostic` is re-materialized via `Location.Create(filePath, span, lineSpan)` — an *external-file* location with **no SourceTree**. The third test above must then read the location via the ORIGINAL source instead: assert `diagnostic.Location.GetLineSpan().Path` is the spec's file path and that `source.Substring(diagnostic.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Length)` starts with `Insn(0xA9`. Write the test that way from the start (the `SourceTree` variant above is shown only to explain the regression being guarded — use the Substring form).

Also update ONE existing test expectation if needed: `InstructionParsingTests` / `RegisterParsingTests` location assertions read location text — convert them to the same Substring-on-source pattern if they used `SourceTree`.

- [ ] **Step 3: Run to verify failure** — collision test crashes or reports CS8785 today; record-decl test generates nothing silently (no CPUGEN009).

- [ ] **Step 4: Implement**

`src/CpuEmulator.Generators/DiagnosticInfo.cs`:

```csharp
using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace CpuEmulator.Generators;

/// <summary>
/// Equatable, tree-free diagnostic carrier for the incremental pipeline. Storing
/// <see cref="Diagnostic"/> in pipeline state roots old syntax trees and defeats caching;
/// this type holds only value data and re-materializes a Diagnostic at report time.
/// </summary>
internal sealed class DiagnosticInfo : IEquatable<DiagnosticInfo>
{
    public string DescriptorId { get; }
    public string FilePath { get; }
    public TextSpan Span { get; }
    public LinePositionSpan LineSpan { get; }
    public string[] Args { get; }

    public DiagnosticInfo(DiagnosticDescriptor descriptor, Location location, params string[] args)
    {
        DescriptorId = descriptor.Id;
        FilePath = location.SourceTree?.FilePath ?? location.GetLineSpan().Path ?? string.Empty;
        Span = location.SourceSpan;
        LineSpan = location.GetLineSpan().Span;
        Args = args;
    }

    public Diagnostic ToDiagnostic()
    {
        var descriptor = SpecDiagnostics.ById(DescriptorId);
        var location = FilePath.Length == 0
            ? Location.None
            : Location.Create(FilePath, Span, LineSpan);
        // Args are pre-stringified; object[] covariance is fine here.
        return Diagnostic.Create(descriptor, location, Args.Cast<object>().ToArray());
    }

    public bool Equals(DiagnosticInfo? other) =>
        other is not null &&
        DescriptorId == other.DescriptorId &&
        FilePath == other.FilePath &&
        Span == other.Span &&
        Args.AsSpan().SequenceEqual(other.Args);

    public override bool Equals(object? obj) => Equals(obj as DiagnosticInfo);

    public override int GetHashCode()
    {
        unchecked
        {
            int hash = DescriptorId.GetHashCode();
            hash = hash * 31 + FilePath.GetHashCode();
            hash = hash * 31 + Span.GetHashCode();
            foreach (string arg in Args)
                hash = hash * 31 + arg.GetHashCode();
            return hash;
        }
    }
}
```

(If `Args.AsSpan().SequenceEqual` is unavailable on netstandard2.0, use `Args.SequenceEqual(other.Args)` from LINQ — equally correct; report which compiled.)

`SpecDiagnostics.cs` — add a lookup:

```csharp
    public static DiagnosticDescriptor ById(string id) => id switch
    {
        "CPUGEN001" => MissingRegisters,
        "CPUGEN002" => InvalidRegister,
        "CPUGEN003" => MissingInstructions,
        "CPUGEN004" => InvalidInstruction,
        "CPUGEN005" => DuplicateOpcode,
        "CPUGEN006" => UnknownMicroOp,
        "CPUGEN007" => RoleViolation,
        "CPUGEN008" => UnknownRegisterInOp,
        "CPUGEN009" => InvalidSpecMetadata,
        "CPUGEN010" => UnsupportedModeOpCombination, // Task 2
        _ => throw new System.ArgumentException($"Unknown diagnostic id '{id}'."),
    };
```

`SpecModel.cs` — `ParsedSpec` now carries `ImmutableArray<DiagnosticInfo>`; `SpecParser` constructs `new DiagnosticInfo(SpecDiagnostics.X, location, args...)` everywhere it currently calls `Diagnostic.Create` (args stringified with the same formatting, e.g. `opcode.Value.ToString("X2")`).

Record-declaration rejection: in `SpecParser.Parse`, if `context.TargetNode` is `RecordDeclarationSyntax` (or the class is not a plain `ClassDeclarationSyntax`), report CPUGEN009 "spec must be a non-record class declaration" and return a null model. (`ForAttributeWithMetadataName`'s predicate currently filters to `ClassDeclarationSyntax` — note `RecordDeclarationSyntax` is NOT a `ClassDeclarationSyntax`, so today records are silently skipped by the predicate. Fix by widening the predicate to `TypeDeclarationSyntax` and rejecting non-class kinds with CPUGEN009 in the transform.)

`CpuSpecGenerator.cs` — collision-safe emission:

```csharp
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var specs = context.SyntaxProvider.ForAttributeWithMetadataName(
            "CpuEmulator.Core.Specification.CpuSpecificationAttribute",
            predicate: static (node, _) => node is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax,
            transform: static (ctx, _) => SpecParser.Parse(ctx));

        var collected = specs.Collect();

        context.RegisterSourceOutput(collected, static (spc, parsedSpecs) =>
        {
            foreach (var parsed in parsedSpecs)
                foreach (var info in parsed.Diagnostics)
                    spc.ReportDiagnostic(info.ToDiagnostic());

            var models = parsedSpecs
                .Where(p => p.Model is not null)
                .Select(p => p.Model!)
                .ToList();

            var collided = models
                .GroupBy(m => $"{m.Namespace}.{m.CpuName}")
                .Where(g => g.Count() > 1)
                .ToList();
            foreach (var group in collided)
                spc.ReportDiagnostic(Diagnostic.Create(
                    SpecDiagnostics.InvalidSpecMetadata, Location.None,
                    $"multiple specs generate the same CPU class '{group.Key}'"));

            var collidedKeys = new HashSet<string>(collided.Select(g => g.Key));
            foreach (var model in models)
                if (!collidedKeys.Contains($"{model.Namespace}.{model.CpuName}"))
                    spc.AddSource($"{model.Namespace}.{model.CpuName}.g.cs", CpuEmitter.Emit(model));
        });
    }
```

Wait — the collision test asserts `Assert.Empty(result.GeneratedTrees)` (NEITHER emitted): with both specs colliding, both are suppressed ✓; the happy-path two-distinct-specs test still gets two trees ✓. Add `using System.Linq;` and `using System.Collections.Generic;` to the generator file.

- [ ] **Step 5: Run tests** — new tests pass; ALL existing generator tests still pass (the conversion must not change any diagnostic id, message, or location semantics — existing location assertions are the regression net). Full suite green.

- [ ] **Step 6: Commit** — `refactor: equatable DiagnosticInfo pipeline with Collect-based collision detection`

---

### Task 2: Parser validation — CPUGEN010 mode/op classes, mnemonic + Flag whitelists

**Files:**
- Modify: `SpecDiagnostics.cs`, `SpecParser.cs`, `AnalyzerReleases.Unshipped.md`
- Create: `tests/CpuEmulator.Tests/Generators/ModeOpValidationTests.cs`

- [ ] **Step 1: Write the failing tests** — each runs a one-instruction spec through `GeneratorTestHost` (reuse the `WithInstructions` replace-helper pattern from `InstructionParsingTests`) and asserts:

| Test | Instruction | Expect |
|---|---|---|
| Store_with_immediate_mode_is_rejected | `Insn(0x99, "STA", AddrMode.Immediate, [Store(Reg.A)])` | CPUGEN010 |
| Jump_with_zero_page_mode_is_rejected | `Insn(0x99, "JMP", AddrMode.ZeroPage, [Jump()])` | CPUGEN010 |
| Branch_with_non_relative_mode_is_rejected | `Insn(0x99, "BNE", AddrMode.Absolute, [BranchIf(Flag.Z, false)])` | CPUGEN010 |
| Relative_requires_exactly_one_branch_op | `Insn(0x99, "BNE", AddrMode.Relative, [BranchIf(Flag.Z, false), SetNZ(Reg.A)])` | CPUGEN010 |
| Load_must_be_first_in_load_class | `Insn(0x99, "LDA", AddrMode.Absolute, [SetNZ(Reg.A), Load(Reg.A)])` | CPUGEN010 |
| Implied_with_memory_op_is_rejected | `Insn(0x99, "STA", AddrMode.Implied, [Store(Reg.A)])` | CPUGEN010 |
| Empty_ops_allowed_only_for_implied | `Insn(0x99, "XYZ", AddrMode.Absolute, [])` | CPUGEN010 |
| Invalid_mnemonic_is_rejected | `Insn(0x99, "BAD\nNAME", ...)` — use a verbatim/escaped newline string | CPUGEN004 |
| Unknown_flag_member_is_rejected | `BranchIf(Flag.B, false)` (B not in C/Z/I/D/V/N — note it won't compile either; assert GeneratorDiagnostics contains CPUGEN006) | CPUGEN006 |
| Valid_subset_passes — the full 11-opcode table (copy from Mos6502Spec) | no CPUGEN diagnostics |

Mnemonic rule: must match `^[A-Z][A-Z0-9]{0,7}$` → else CPUGEN004 reason "mnemonic must be 1-8 uppercase letters/digits".

- [ ] **Step 2: Verify failures.**

- [ ] **Step 3: Implement** in `SpecParser`:
- New descriptor:
```csharp
    public static readonly DiagnosticDescriptor UnsupportedModeOpCombination = Make(
        "CPUGEN010", "Unsupported mode/op combination",
        "Instruction '{0}': {1}");
```
- Classification function (after an instruction parses): determine class from ops — `Load` first → load class (remaining ops must be register ops); single `Store` → store; single `Jump` → jump; single `BranchIf` → branch; all register ops (Transfer/Increment/SetNZ, possibly empty) → register class. Then validate against mode: register→Implied only; load→Immediate/ZeroPage/Absolute; store→ZeroPage/Absolute; jump→Absolute; branch→Relative; Implied accepts only register class; empty ops only valid as register class (Implied). Any violation → CPUGEN010 at the instruction element's location with a human reason ("Store requires ZeroPage or Absolute mode", etc.).
- Flag whitelist `{C,Z,I,D,V,N}` enforced where Flag members parse (CPUGEN006 with the flag name).
- Mnemonic regex check (compile once, `static readonly Regex`); netstandard2.0 has `System.Text.RegularExpressions` — or use a simple char loop to avoid the Regex dependency in a generator (preferred: char loop).
- Add CPUGEN010 to `AnalyzerReleases.Unshipped.md` and to `SpecDiagnostics.ById`.

- [ ] **Step 4: Tests pass; full suite green. Commit** — `feat: validate mode/op classes (CPUGEN010), mnemonics, and flag members at parse time`

> **Post-review amendments (applied after Task 2, pre-Task-3):**
> - `6db21b4` — `EquatableArray<T>` wraps every collection in pipeline state so identical
>   reparses hit the incremental cache (pinned by a trackIncrementalGeneratorSteps test);
>   CPUGEN011 validates each micro-op argument against its expected kind (Reg vs Flag vs
>   Bool), closing the `SetNZ(Flag.Z)` / `BranchIf(Reg.A, …)` emitter-crash hole at parse
>   time; collision CPUGEN009 now reports per-collider at each class identifier via
>   tree-free `LocationInfo` (the plan's code block above shows the original single
>   `Location.None` diagnostic); diagnostic gating simplified to any-diagnostic-nulls-model.
> - `a5ba39c` — critical test-infrastructure fix: raw string literals inherit their file's
>   line endings, so CRLF-smudged needles silently no-op'd `Replace` and ran rejection tests
>   against the unmodified valid spec. `GeneratorTestHost.ReplaceSection` now normalizes both
>   sides to LF and asserts the replace fired; `.gitattributes` pins `*.cs` to LF.

---

### Task 3: Emitter — load class live (LDA #/zp/abs, LDX #) + trace infrastructure

**Files:**
- Modify: `CpuEmitter.cs`
- Create: `tests/CpuEmulator.Tests/Mos6502/TracingAddressSpace.cs`, `tests/CpuEmulator.Tests/Mos6502/Mos6502TraceTests.cs`

- [ ] **Step 1: Trace infrastructure**

`tests/CpuEmulator.Tests/Mos6502/TracingAddressSpace.cs`:

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Tests.Mos6502;

internal sealed record BusAccess(uint Address, byte Value, bool IsRead)
{
    public override string ToString() => $"{(IsRead ? "R" : "W")} {Address:X4}={Value:X2}";
}

/// <summary>
/// Records every bus access in order. Cycle-number convention (recorded in the 2a plan):
/// the CPU charges _cycles BEFORE the access, so trace entry N corresponds to cycle N+1
/// of the instruction stream; tests assert the ordered access list and the cycle total
/// separately rather than per-entry cycle stamps.
/// </summary>
internal sealed class TracingAddressSpace(IAddressSpace inner) : IAddressSpace
{
    public List<BusAccess> Trace { get; } = [];

    public AddressSpaceKind Kind => inner.Kind;
    public int AddressBits => inner.AddressBits;

    public byte Read8(uint address)
    {
        byte value = inner.Read8(address);
        Trace.Add(new BusAccess(address, value, true));
        return value;
    }

    public void Write8(uint address, byte value)
    {
        Trace.Add(new BusAccess(address, value, false));
        inner.Write8(address, value);
    }

    public void MapMemory(uint start, byte[] backing, bool writable) =>
        inner.MapMemory(start, backing, writable);

    public void MapPeripheral(uint start, uint length, IPeripheral peripheral) =>
        inner.MapPeripheral(start, length, peripheral);
}
```

`Mos6502TraceTests.cs` skeleton + helper:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

public class Mos6502TraceTests
{
    /// <summary>CPU with 64 KiB RAM, program bytes at 0x0200, PC set there, tracing bus.</summary>
    private static (Mos6502Cpu Cpu, TracingAddressSpace Bus) NewCpu(params byte[] program)
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        for (uint i = 0; i < program.Length; i++)
            inner.Write8(0x0200 + i, program[i]);
        var bus = new TracingAddressSpace(inner);
        var cpu = new Mos6502Cpu(bus);
        cpu.SetRegister("PC", 0x0200);
        return (cpu, bus);
    }

    private static void AssertTrace(TracingAddressSpace bus, params BusAccess[] expected) =>
        Assert.Equal(expected, bus.Trace);
}
```

- [ ] **Step 2: Write the failing load-class trace tests** (literal traces; `R`/`W` shorthand = `new BusAccess(addr, value, true/false)`):

| Test | Program @0200 / setup | Expected trace | Asserts |
|---|---|---|---|
| LDA_immediate_2_cycles | `A9 42` | R 0200=A9, R 0201=42 | cycles=2, A=0x42, PC=0x0202, P: Z=0,N=0 |
| LDA_immediate_zero_sets_Z | `A9 00` | (same shape) | A=0, P bit1 set, bit7 clear |
| LDA_immediate_negative_sets_N | `A9 80` | | P bit7 set, bit1 clear |
| LDA_zero_page_3_cycles | `A5 10`, RAM[0x10]=0x5A | R 0200=A5, R 0201=10, R 0010=5A | cycles=3, A=0x5A |
| LDA_absolute_4_cycles | `AD 34 12`, RAM[0x1234]=0x77 | R 0200=AD, R 0201=34, R 0202=12, R 1234=77 | cycles=4, A=0x77 |
| LDX_immediate_2_cycles | `A2 07` | R 0200=A2, R 0201=07 | X=7, cycles=2 |

Flag assertions: read `cpu.GetRegister("P")` and check bits (Z=0x02, N=0x80) — write a tiny local helper `AssertNZ(cpu, bool n, bool z)`.

- [ ] **Step 3: Verify failure** — all dispatch to `HandleUndefinedOpcode` today → `UndefinedOpcodeException`.

- [ ] **Step 4: Implement emission.** `CpuEmitter` changes:
- `_cycles` becomes `private` (carry-forward); header contract gains `WriteBus(uint, byte) (which increments _cycles)`.
- `Execute` switch gains `case 0x{opcode:X2}: Op{opcode:X2}(); break;` per instruction; per-opcode methods emitted from the templates. Emit a doc line per method: `/// <summary>0xA9 LDA Immediate — 2 cycles.</summary>` (cycle count = 1 + template cycles; branch methods say "2-4 cycles").
- Implement the **load class** for Immediate/ZeroPage/Absolute and the register-op application helpers (`SetNZ`, `Transfer`, `Increment`, `Load`) — emit straight-line statements, e.g. for `0xAD LDA abs`:

```csharp
    private void OpAD()
    {
        uint lo = ReadBus(PC);
        PC = unchecked((ushort)(PC + 1));
        uint hi = ReadBus(PC);
        PC = unchecked((ushort)(PC + 1));
        uint ea = lo | (hi << 8);
        byte data = ReadBus(ea);
        A = data;
        P = unchecked((byte)((P & 0x7D) | (A == 0 ? 0x02 : 0x00) | (A & 0x80)));
    }
```

The emitter writes these lines from the model (register names substituted from the spec; the Status register's name from its role). Other classes (store/jump/branch/register) keep dispatching to `HandleUndefinedOpcode` until Tasks 4–5 — emit only the classes implemented so far and leave the rest in the default arm (the CPUGEN010 validation guarantees the model only contains the five known classes).

- [ ] **Step 5: Tests pass (6 new). Full suite green (existing 2a generator tests that assert the dispatch summary comment still pass — adjust ONLY if an assertion hard-coded "Per-opcode execution methods are emitted in chunk 2b", which this task removes; update that string assertion to match the new reality).**

- [ ] **Step 6: Commit** — `feat: emit load-class opcode bodies; cycle-exact trace tests for LDA/LDX`

---

### Task 4: Emitter — register, store, jump classes (TAX, INX, NOP, STA zp/abs, JMP)

**Files:** modify `CpuEmitter.cs`; extend `Mos6502TraceTests.cs`.

- [ ] **Step 1: Failing tests:**

| Test | Program / setup | Expected trace | Asserts |
|---|---|---|---|
| TAX_2_cycles_with_dummy_read | A=0x42 via SetRegister; `AA EA` | R 0200=AA, R 0201=EA (dummy) | X=0x42, PC=0x0201 (dummy read does NOT advance PC), cycles=2 |
| INX_wraps_FF_to_00_sets_Z | X=0xFF; `E8` | R 0200=E8, R 0201=00 (dummy) | X=0, Z set, N clear |
| NOP_2_cycles | `EA` | R 0200=EA, R 0201=00 (dummy) | nothing else changes; cycles=2 |
| STA_zero_page_writes_A | A=0x99; `85 10` | R 0200=85, R 0201=10, **W 0010=99** | RAM[0x10]=0x99, cycles=3, P unchanged |
| STA_absolute_writes_A | A=0x99; `8D 34 12` | R 0200=8D, R 0201=34, R 0202=12, W 1234=99 | cycles=4 |
| JMP_absolute_3_cycles | `4C 00 80` | R 0200=4C, R 0201=00, R 0202=80 | PC=0x8000, cycles=3 — NO read at 0x8000 |

(Dummy-read VALUES in traces are whatever RAM holds — set the byte after the opcode explicitly where asserted, e.g. TAX test writes `EA` at 0x0201 so the dummy read's value is deterministic; for `E8`/`EA` single-byte programs the next byte is 0x00 from zeroed RAM.)

- [ ] **Step 2–3: Implement register/store/jump emission per the templates; tests pass; suite green.**

- [ ] **Step 4: Commit** — `feat: emit register/store/jump opcode bodies with dummy-read fidelity`

---

### Task 5: Emitter — branch class (BNE: not-taken / taken / page-cross)

**Files:** modify `CpuEmitter.cs`; extend `Mos6502TraceTests.cs`.

- [ ] **Step 1: Failing tests** (the chunk's crown jewels — silicon-exact branch timing):

| Test | Setup | Expected trace | Asserts |
|---|---|---|---|
| BNE_not_taken_2_cycles | Z set (P via SetRegister 0x36); `D0 05` | R 0200=D0, R 0201=05 | PC=0x0202, cycles=2 |
| BNE_taken_3_cycles | Z clear (P=0x34); `D0 05` | R 0200=D0, R 0201=05, R 0202=xx (dummy) | PC=0x0207, cycles=3 |
| BNE_taken_backward_3_cycles | Z clear; `D0 FC` (-4) | …, R 0202=xx | PC=0x01FE… **wait — 0x0202-4=0x01FE crosses from 0x02xx to 0x01xx → that's the page-cross case.** Use a backward branch that stays in page: program at 0x0210: `D0 FC` → target 0x0212-4=0x020E, same page → 3 cycles. Place program accordingly (helper writes at 0x0200; write these two bytes at 0x0210 manually and set PC=0x0210). |
| BNE_taken_page_cross_4_cycles | Z clear; at 0x02F0: `D0 20` → after operand PC=0x02F2, target 0x0312 | R 02F0=D0, R 02F1=20, R 02F2=xx (dummy), **R 0212=xx (wrong-page dummy: old page 0x02, new lo 0x12)** | PC=0x0312, cycles=4 |

(For the page-cross test write the two bytes at 0x02F0 via the inner space before wrapping — extend the helper with an overload `NewCpuAt(ushort origin, params byte[] program)`.)

- [ ] **Step 2–3: Implement branch emission (code from the template section above, with the flag bit from the emitter's Flag map); tests pass; suite green.**

- [ ] **Step 3b: SANITY GATE — run the FULL suite and confirm the 2a skeleton tests still pass unmodified** (they were future-proofed with JAM opcodes precisely so this chunk wouldn't touch them).

- [ ] **Step 4: Commit** — `feat: emit branch bodies with taken/page-cross cycle fidelity`

---

### Task 6: Disassembler emission

**Files:** modify `CpuEmitter.cs`; create `tests/CpuEmulator.Tests/Generators/DisassemblerEmissionTests.cs`; extend `Mos6502TraceTests.cs` or new test class for behavior.

- [ ] **Step 1: Failing tests** (behavioral, against the real generated `Mos6502Cpu.Disassemble`):

```csharp
    [Theory]
    [InlineData(0xA9, 0x42, 0x00, "LDA #$42")]
    [InlineData(0xA5, 0x10, 0x00, "LDA $10")]
    [InlineData(0xAD, 0x34, 0x12, "LDA $1234")]
    [InlineData(0x8D, 0x34, 0x12, "STA $1234")]
    [InlineData(0xAA, 0x00, 0x00, "TAX")]
    [InlineData(0x4C, 0x00, 0x80, "JMP $8000")]
    [InlineData(0xD0, 0xFC, 0x00, "BNE *-4")]
    [InlineData(0xD0, 0x05, 0x00, "BNE *+5")]
    [InlineData(0xFF, 0x00, 0x00, "???")]
    public void Disassemble_formats_by_addressing_mode(byte opcode, byte lo, byte hi, string expected) =>
        Assert.Equal(expected, Mos6502Cpu.Disassemble(opcode, lo, hi));
```

Plus one generator-harness test asserting the generated text contains `public static string Disassemble(byte opcode, byte operandLo, byte operandHi)`.

- [ ] **Step 2: Implement:** emitter generates a static method — `switch (opcode)` over the table returning interpolated strings per mode: Implied → mnemonic; Immediate → `$"{m} #${operandLo:X2}"`; ZeroPage → `$"{m} ${operandLo:X2}"`; Absolute → `$"{m} ${operandHi:X2}{operandLo:X2}"`; Relative → `$"{m} *{(sbyte)operandLo:+0;-0}"`; default → `"???"`. Mnemonics pass through `SymbolDisplay.FormatLiteral`-style escaping is unnecessary now (mnemonic whitelist from Task 2 guarantees `[A-Z0-9]{1,8}`) — note that dependency in a comment.

- [ ] **Step 3: Tests pass; suite green. Commit** — `feat: emit disassembler from the spec table`

> **Pre-3b restructure item (recorded from Group-2 quality review):** instruction
> classification currently runs twice (CPUGEN010 validation in SpecParser; ClassifyInstruction
> in CpuEmitter) — before 3b's class growth, classify once in the parser and carry the class
> on `InstructionModel` so parser and emitter cannot drift.

---

### Task 7: Integration tests — programs run end-to-end

**Files:** create `tests/CpuEmulator.Tests/Mos6502/Mos6502ProgramTests.cs`.

- [ ] **Step 1: Write the tests** (these are the chunk's integration tier):

```csharp
    [Fact]
    public void Countdown_loop_executes_with_exact_cycle_total()
    {
        // 0200: A2 05     LDX #$05      (2 cy)
        // 0202: CA?? not in subset — use INX-toward-zero instead:
        // X counts 0xFB..0xFF then wraps to 0 setting Z:
        // 0200: A2 FB     LDX #$FB      2
        // 0202: E8        INX           2     ┐
        // 0203: D0 FD     BNE $0202     3/2   ┘ ×5 iterations (4 taken, last not-taken)
        var (cpu, _) = NewCpu(0xA2, 0xFB, 0xE8, 0xD0, 0xFD);

        while (cpu.GetRegister("PC") != 0x0205)
            cpu.Step();

        Assert.Equal(0ul, cpu.GetRegister("X"));
        // cycles: LDX(2) + 5×INX(2) + 4×BNE-taken(3) + 1×BNE-not-taken(2) = 26
        Assert.Equal(26, cpu.CycleCount);
    }

    [Fact]
    public void Store_load_roundtrip_through_memory()
    {
        // LDA #$5A; STA $1234; LDA #$00; LDA $1234 → A=0x5A again
        var (cpu, _) = NewCpu(0xA9, 0x5A, 0x8D, 0x34, 0x12, 0xA9, 0x00, 0xAD, 0x34, 0x12);
        for (int i = 0; i < 4; i++)
            cpu.Step();

        Assert.Equal(0x5Aul, cpu.GetRegister("A"));
    }

    [Fact]
    public void Program_runs_inside_a_Machine_via_reset_vector()
    {
        var machine = Machine.Create("breadboard")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
            .Build();
        var space = machine.Space(AddressSpaceKind.Program);
        byte[] program = [0xA9, 0x42, 0x8D, 0x00, 0x10, 0x4C, 0x05, 0x02]; // LDA;STA;JMP self
        for (uint i = 0; i < program.Length; i++)
            space.Write8((uint)(0x0200 + i), program[i]);
        space.Write8(0xFFFC, 0x00);
        space.Write8(0xFFFD, 0x02);

        machine.Reset();
        machine.Run(100);

        Assert.Equal(0x42, space.Read8(0x1000));
        Assert.Equal(0x0205ul, machine.Cpu.GetRegister("PC")); // parked on JMP-self
    }
```

(Verify the countdown-loop helper math before trusting it: 0xFB+5 = 0x100 → X wraps to 0 on the 5th INX; BNE taken after INX 1–4 — all same-page backward branches (target 0x0202 from 0x0205, page 0x02) → 3 cycles each; final BNE not-taken → 2. If the assert fails, recompute by hand against the implementation trace rather than adjusting blindly — the trace tests make the per-instruction timing trustworthy.)

- [ ] **Step 2: Tests pass; full suite green; `dotnet build --no-incremental` 0 warnings.**

- [ ] **Step 3: Commit** — `test: end-to-end program execution on the generated 6502`

---

### Task 8: Docs, final verification, push, PR

- [ ] README `## Status`: the subset now EXECUTES with cycle-exact traces; next is chunk 3a (spec importer) then 3b (full 6502 + SingleStepTests).
- [ ] Full verification: build 0 warnings, full suite green; report final test count.
- [ ] Commit `docs: note interpreter-emission status in README`; do NOT push — the controller runs the final whole-branch review first, then supplies the PR body. PR base is `feat/m1-generator-frontend` (stacked).

---

## Plan self-review (completed at write time)

- **Spec coverage:** artifact ② (per-opcode interpreter, cycle-true per §5/§6 accuracy decision) ✓ Tasks 3–5; artifact ④ (disassembler) ✓ Task 6; §7 fail-at-build extended (CPUGEN010) ✓ Task 2; carry-forwards all addressed ✓ Task 1 + emitter tasks; deliberate deviations (switch dispatch, fields-on-class) recorded in header.
- **Placeholder scan:** templates and trace tables carry literal expected values; Task 3 shows a complete emitted-body example; remaining per-opcode emissions are mechanical instantiations of the stated templates over the model — the trace tables define their exact observable behavior, which is the contract.
- **Type consistency:** `BusAccess(uint, byte, bool)` matches `IAddressSpace` signatures; `TracingAddressSpace` wraps rather than subclasses (AddressSpace is sealed); flag masks match the Flag enum's hardware values (Z=0x02 ↔ bit 1, N=0x80 ↔ bit 7); P-register test values 0x34/0x36 differ exactly in the Z bit.
- **Known risks:** the cycle-count arithmetic in Task 7's loop test is hand-derived — the plan instructs recomputation against traces on failure rather than blind adjustment; emitter code is written blind (same accepted risk as 2a, same TDD gates).
