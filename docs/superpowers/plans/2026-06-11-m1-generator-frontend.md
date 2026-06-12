# M1 Chunk 2a: Spec DSL + Source-Generator Front-End — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the CPU-spec DSL (typed C# tables in `CpuEmulator.Core.Specification`), the Roslyn incremental source generator that parses it with build-time diagnostics, and the generated `Mos6502Cpu` skeleton (state, introspection, Step/Run plumbing, undefined-opcode policy) — wired end-to-end in a real `CpuEmulator.Cpus.Mos6502` project. PR #2 of Milestone 1.

**Architecture:** A `[CpuSpecification]`-attributed static class holds `Registers` and `Instructions` tables written with constrained factory calls (the analyzable DSL). The generator (`netstandard2.0`, `IIncrementalGenerator`) parses syntax into a `SpecModel`, reports `CPUGEN0xx` diagnostics for every misuse (spec §7: fail at build, never mid-run), and emits a `sealed partial class Mos6502Cpu : ICpuCore` containing register fields, `RegisterNames`/`Get`/`SetRegister`, `CycleCount`, `Step` (opcode fetch + dispatch), and `Run`. A hand-written partial supplies the bus, `Reset`, and policy. **No instruction executes in this PR** — `Execute` dispatches every opcode to the undefined-opcode policy; per-opcode emission is chunk 2b.

**Tech Stack:** .NET 10, Roslyn `Microsoft.CodeAnalysis.CSharp` 4.12.0 (generator + test host), xUnit. Generator tested in-memory via `CSharpGeneratorDriver` (no snapshot files; assertions on diagnostics, generated text, and compilation success).

**Spec:** `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md` §5 (ISA spec + constrained DSL), §7 (error handling). **Plan series update:** M1 is now six plans — 1: core contracts ✅ (PR #1), **2a: this plan**, 2b: interpreter emission + subset live, 3a: spec-importer tool (opcode rows generated from a curated machine-readable dataset; semantics hand-authored once per mnemonic — "mostly automated" spec creation), 3b: full 6502 + TomHarte (+ IL-emitter artifact, generate-only), 4: peripherals + host. A TomHarte-derived spec linter (inferring opcode behavior from vectors to cross-check the dataset) is parked at the M4+ horizon.

**Carry-forwards honored here** (from PR #1 reviews): `ICpuCore.Step()` gets its cycle-advance contract documented; generated cores always advance `CycleCount` per `Step` (no-progress guard safe); introspection follows the pinned ArgumentException/truncation contract.

---

## File structure

```
src/CpuEmulator.Generators/
    CpuEmulator.Generators.csproj      — netstandard2.0, IsRoslynComponent, Roslyn 4.12 PrivateAssets
    IsExternalInit.cs                  — record polyfill for netstandard2.0
    CpuSpecGenerator.cs                — IIncrementalGenerator entry point
    SpecModel.cs                       — parsed model records (+ ParsedSpec envelope)
    SpecDiagnostics.cs                 — CPUGEN001..CPUGEN007 descriptors
    SpecParser.cs                      — syntax → SpecModel with diagnostics
    CpuEmitter.cs                      — SpecModel → generated C# source
src/CpuEmulator.Core/Specification/
    CpuSpecificationAttribute.cs
    RegisterRole.cs / RegisterDef.cs
    AddrMode.cs / Reg.cs / Flag.cs
    Op.cs                              — micro-op record hierarchy
    InstructionDef.cs
    Spec.cs                            — DSL factory methods (Insn, Load, Store, …)
    UndefinedOpcodePolicy.cs
    UndefinedOpcodeException.cs
src/CpuEmulator.Cpus.Mos6502/
    CpuEmulator.Cpus.Mos6502.csproj    — net10.0, AOT, unsafe, analyzer-refs Generators
    Mos6502Spec.cs                     — the 11-opcode table (DSL)
    Mos6502Cpu.cs                      — hand-written partial: bus, Reset, policy, line recording
src/CpuEmulator.Core/ICpuCore.cs       — MODIFY: Step() doc line
tests/CpuEmulator.Tests/
    CpuEmulator.Tests.csproj           — MODIFY: + Roslyn pkg, + Generators/Mos6502 refs
    Generators/GeneratorTestHost.cs    — in-memory compile + driver harness
    Generators/GeneratorHappyPathTests.cs
    Generators/RegisterParsingTests.cs
    Generators/InstructionParsingTests.cs
    Mos6502/Mos6502SkeletonTests.cs    — behavioral tests against the REAL generated type
```

Design decisions locked by this plan:

- **DSL constraint:** spec tables may contain ONLY: collection expressions, `new(...)`/`Insn(...)`-style creations and factory invocations from `CpuEmulator.Core.Specification.Spec`, literal arguments, and enum member accesses. Anything else is a `CPUGEN0xx` build error. This is the "Roslyn reads syntax, not runtime values" constraint from spec §5, made enforceable.
- **Generated/hand-written partial contract:** the generated half implements state + introspection + `Step`/`Run`/`Execute`; it CALLS `ReadBus(uint)` and `HandleUndefinedOpcode(byte)` which the hand-written partial MUST provide (along with `Reset`, IRQ/NMI line inputs, and the constructor). The emitter writes this contract into a header comment of every generated file.
- **Cycle accounting rule:** every bus access costs exactly one cycle, charged inside `ReadBus`/`WriteBus`. `Step` charges nothing itself — its opcode fetch goes through `ReadBus`.
- **IRQ/NMI lines are recorded but not yet serviced** (no interrupt sequence until chunk 3, where the full spec lands). Documented in the hand-written partial.
- **`UndefinedOpcodePolicy`:** `Throw` (default; `UndefinedOpcodeException` carries opcode + address) or `Nop` (burns one extra cycle so `Run` always progresses). The spec-§7 user-callback variant is deferred until a consumer exists.
- **Attribute → class naming:** `[CpuSpecification("mos6502")]` on `Mos6502Spec` generates `Mos6502Cpu` (strip trailing `Spec`, append `Cpu`); overridable via `CpuName = "..."` named argument.

---

### Task 1: Branch + two new projects

**Files:**
- Create: `src/CpuEmulator.Generators/CpuEmulator.Generators.csproj`
- Create: `src/CpuEmulator.Generators/IsExternalInit.cs`
- Create: `src/CpuEmulator.Cpus.Mos6502/CpuEmulator.Cpus.Mos6502.csproj`
- Modify: `tests/CpuEmulator.Tests/CpuEmulator.Tests.csproj`, solution

- [ ] **Step 1: Branch**

```bash
git checkout -b feat/m1-generator-frontend
```

- [ ] **Step 2: Create the generator project.** `src/CpuEmulator.Generators/CpuEmulator.Generators.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <IsRoslynComponent>true</IsRoslynComponent>
    <EnforceExtendedAnalyzerRules>true</EnforceExtendedAnalyzerRules>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.CodeAnalysis.CSharp" Version="4.12.0" PrivateAssets="all" />
  </ItemGroup>

</Project>
```

`src/CpuEmulator.Generators/IsExternalInit.cs` (records on netstandard2.0):

```csharp
// Polyfill so C# records compile against netstandard2.0.
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}
```

- [ ] **Step 3: Create the 6502 project.** `src/CpuEmulator.Cpus.Mos6502/CpuEmulator.Cpus.Mos6502.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsAotCompatible>true</IsAotCompatible>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <CompilerGeneratedFilesOutputPath>$(BaseIntermediateOutputPath)generated</CompilerGeneratedFilesOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\CpuEmulator.Core\CpuEmulator.Core.csproj" />
    <ProjectReference Include="..\CpuEmulator.Generators\CpuEmulator.Generators.csproj"
                      OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
  </ItemGroup>

</Project>
```

(`AllowUnsafeBlocks` is for chunk 2b's `delegate*` dispatch; harmless now. `EmitCompilerGeneratedFiles` makes generated source inspectable under `obj/generated/` for debugging and review.)

- [ ] **Step 4: Wire solution + test project**

```bash
dotnet sln add src/CpuEmulator.Generators src/CpuEmulator.Cpus.Mos6502
dotnet add tests/CpuEmulator.Tests reference src/CpuEmulator.Generators src/CpuEmulator.Cpus.Mos6502
dotnet add tests/CpuEmulator.Tests package Microsoft.CodeAnalysis.CSharp --version 4.12.0
```

(If 4.12.0 fails to restore on this SDK, bump both the generator's and the tests' reference to the lowest version that restores — keep them identical — and report the substitution.)

- [ ] **Step 5: Verify** — `dotnet build` succeeds, 0 warnings; `dotnet test` still 59/59.

- [ ] **Step 6: Commit** — `chore: add Generators and Cpus.Mos6502 projects`

---

### Task 2: The spec DSL types (+ ICpuCore.Step doc carry-forward)

Pure contracts/data — compile check is the gate (behavior is tested from Task 3 onward through the generator and the generated CPU).

**Files:**
- Create: everything under `src/CpuEmulator.Core/Specification/` listed in the file structure
- Modify: `src/CpuEmulator.Core/ICpuCore.cs`

- [ ] **Step 1: Write the DSL type files**

`src/CpuEmulator.Core/Specification/CpuSpecificationAttribute.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>Marks a class holding CPU spec tables (Registers, Instructions) for the source
/// generator. The generated CPU class is named by stripping a trailing "Spec" from the class
/// name and appending "Cpu", unless <see cref="CpuName"/> overrides it.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class CpuSpecificationAttribute(string architecture) : Attribute
{
    /// <summary>Architecture identifier surfaced as <c>ICpuCore.Architecture</c>, e.g. "mos6502".</summary>
    public string Architecture { get; } = architecture;

    /// <summary>Optional explicit name for the generated CPU class.</summary>
    public string? CpuName { get; set; }
}
```

`src/CpuEmulator.Core/Specification/RegisterRole.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

public enum RegisterRole
{
    General,
    ProgramCounter,
    Status,
    StackPointer,
}
```

`src/CpuEmulator.Core/Specification/RegisterDef.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>One architectural register. <paramref name="Bits"/> must be 8 or 16 (wider
/// registers arrive with a 16/32-bit CPU). Exactly one register must have the
/// <see cref="RegisterRole.ProgramCounter"/> role.</summary>
public sealed record RegisterDef(string Name, int Bits, RegisterRole Role = RegisterRole.General);
```

`src/CpuEmulator.Core/Specification/AddrMode.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>Addressing modes supported by the chunk-2 subset. Each mode is a fixed
/// cycle-by-cycle bus pattern the generator expands (spec §5: modes are micro-op templates).</summary>
public enum AddrMode
{
    Implied,
    Immediate,
    ZeroPage,
    Absolute,
    Relative,
}
```

`src/CpuEmulator.Core/Specification/Reg.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>Operand-addressable registers referenced by micro-ops. Names must match
/// entries in the spec's Registers table.</summary>
public enum Reg
{
    A,
    X,
    Y,
    S,
}
```

`src/CpuEmulator.Core/Specification/Flag.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>Status-register flags (6502 P-register layout: N V - B D I Z C).</summary>
public enum Flag
{
    C,
    Z,
    I,
    D,
    V,
    N,
}
```

`src/CpuEmulator.Core/Specification/Op.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>Base of the closed micro-op vocabulary. The generator pattern-matches the
/// factory calls in <see cref="Spec"/> by name; these records exist so spec tables
/// type-check and tooling can navigate them.</summary>
public abstract record Op;

public sealed record LoadRegOp(Reg Target) : Op;
public sealed record StoreRegOp(Reg Source) : Op;
public sealed record TransferOp(Reg Source, Reg Target) : Op;
public sealed record IncrementOp(Reg Target) : Op;
public sealed record SetNZOp(Reg Source) : Op;
public sealed record JumpOp : Op;
public sealed record BranchIfOp(Flag Flag, bool When) : Op;
```

`src/CpuEmulator.Core/Specification/InstructionDef.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>One instruction-table row: opcode byte, mnemonic, addressing mode, and the
/// micro-op sequence executed after the mode's bus pattern resolves the operand.</summary>
public sealed record InstructionDef(byte Opcode, string Mnemonic, AddrMode Mode, Op[] Ops);
```

`src/CpuEmulator.Core/Specification/Spec.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>DSL factories for spec tables. The source generator recognizes calls to these
/// BY NAME with literal/enum arguments only — no variables, no computed expressions
/// (CPUGEN004/CPUGEN006 otherwise). This is the constrained-DSL contract from the design
/// spec (§5): specs must be statically analyzable.</summary>
public static class Spec
{
    public static InstructionDef Insn(byte opcode, string mnemonic, AddrMode mode, Op[] ops) =>
        new(opcode, mnemonic, mode, ops);

    public static Op Load(Reg target) => new LoadRegOp(target);
    public static Op Store(Reg source) => new StoreRegOp(source);
    public static Op Transfer(Reg source, Reg target) => new TransferOp(source, target);
    public static Op Increment(Reg target) => new IncrementOp(target);
    public static Op SetNZ(Reg source) => new SetNZOp(source);
    public static Op Jump() => new JumpOp();
    public static Op BranchIf(Flag flag, bool when) => new BranchIfOp(flag, when);
}
```

`src/CpuEmulator.Core/Specification/UndefinedOpcodePolicy.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>What a CPU does when it fetches an opcode its spec does not define (spec §7).
/// A user-callback variant is deferred until a consumer needs it.</summary>
public enum UndefinedOpcodePolicy
{
    /// <summary>Throw <see cref="UndefinedOpcodeException"/>. Default — loud, for development.</summary>
    Throw,

    /// <summary>Treat as a 2-cycle NOP (fetch + one internal cycle) so execution always progresses.</summary>
    Nop,
}
```

`src/CpuEmulator.Core/Specification/UndefinedOpcodeException.cs`:

```csharp
namespace CpuEmulator.Core.Specification;

/// <summary>Raised by <see cref="UndefinedOpcodePolicy.Throw"/> when an unspecified opcode
/// is fetched. A guest-world event escalated to a host exception by explicit opt-in policy.</summary>
public sealed class UndefinedOpcodeException(byte opcode, uint address)
    : EmulationException($"Undefined opcode 0x{opcode:X2} at address 0x{address:X4}.")
{
    public byte Opcode { get; } = opcode;
    public uint Address { get; } = address;
}
```

- [ ] **Step 2: Document Step's cycle contract** (PR-#1 carry-forward). In `src/CpuEmulator.Core/ICpuCore.cs`, replace `/// <summary>Execute exactly one instruction.</summary>` with:

```csharp
    /// <summary>Execute exactly one instruction. Always advances <see cref="CycleCount"/> by
    /// at least one cycle — including for undefined opcodes and (future) halted states — so
    /// drivers' no-progress guards never fire on a healthy core.</summary>
```

- [ ] **Step 3: Verify** — `dotnet build`, 0 warnings.

- [ ] **Step 4: Commit** — `feat: add CPU spec DSL types and undefined-opcode policy`

> **Post-review amendments (applied after Tasks 1–2):** `Flag` enum members carry explicit
> hardware bit positions (C=0, Z=1, I=2, D=3, V=6, N=7) so masks derive directly from values;
> UndefinedOpcodeException doc no longer mislabels the Throw default as "opt-in";
> InstructionDef documents its types as inert syntax carriers. Accepted divergence from design
> spec §5: addressing modes are a closed `AddrMode` enum rather than a `Mode(...)` combinator —
> revisit only when a second architecture (M3) needs open-ended modes.

---

### Task 3: Generator test harness + generator skeleton

**Files:**
- Create: `tests/CpuEmulator.Tests/Generators/GeneratorTestHost.cs`
- Create: `tests/CpuEmulator.Tests/Generators/GeneratorHappyPathTests.cs`
- Create: `src/CpuEmulator.Generators/CpuSpecGenerator.cs`
- Create: `src/CpuEmulator.Generators/SpecModel.cs`
- Create: `src/CpuEmulator.Generators/SpecDiagnostics.cs`
- Create: `src/CpuEmulator.Generators/SpecParser.cs` (skeleton — attribute/name handling only)
- Create: `src/CpuEmulator.Generators/CpuEmitter.cs` (skeleton — empty partial class)

- [ ] **Step 1: Write the harness**

`tests/CpuEmulator.Tests/Generators/GeneratorTestHost.cs`:

```csharp
using System.Collections.Immutable;
using CpuEmulator.Core;
using CpuEmulator.Generators;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CpuEmulator.Tests.Generators;

internal sealed record GeneratorRunResult(
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<SyntaxTree> GeneratedTrees,
    ImmutableArray<Diagnostic> CompilationDiagnostics)
{
    public string GeneratedText => string.Concat(GeneratedTrees.Select(t => t.ToString()));

    public ImmutableArray<Diagnostic> AllErrors =>
        [.. GeneratorDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error),
         .. CompilationDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)];
}

internal static class GeneratorTestHost
{
    private static readonly ImmutableArray<MetadataReference> s_references = BuildReferences();

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        // Reference the full TPA closure of the running test process (net10 BCL facades)
        // plus CpuEmulator.Core, so spec sources compile exactly like a real consumer.
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
        return [.. tpa, MetadataReference.CreateFromFile(typeof(ICpuCore).Assembly.Location)];
    }

    public static GeneratorRunResult Run(string source)
    {
        var compilation = CSharpCompilation.Create(
            "SpecUnderTest",
            [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest))],
            s_references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(new CpuSpecGenerator());
        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var updated, out _);
        var runResult = driver.GetRunResult();

        return new GeneratorRunResult(
            runResult.Diagnostics,
            [.. runResult.GeneratedTrees],
            updated.GetDiagnostics());
    }
}
```

- [ ] **Step 2: Write the failing happy-path test**

`tests/CpuEmulator.Tests/Generators/GeneratorHappyPathTests.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CpuEmulator.Tests.Generators;

public class GeneratorHappyPathTests
{
    // A complete, valid spec + the minimal hand-written partial the generated half requires.
    // Shared by parsing/emission tests (later tasks mutate pieces of it).
    public const string ValidSpecSource = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace TestCpu;

        [CpuSpecification("test6502")]
        public static class Tiny6502Spec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8),
                new("X", 8),
                new("S", 8, RegisterRole.StackPointer),
                new("P", 8, RegisterRole.Status),
                new("PC", 16, RegisterRole.ProgramCounter),
            ];

            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
                Insn(0xEA, "NOP", AddrMode.Implied, []),
            ];
        }

        public sealed partial class Tiny6502Cpu
        {
            private readonly IAddressSpace _bus;
            public Tiny6502Cpu(IAddressSpace bus) => _bus = bus;
            public void Reset() { }
            public void SetIrqLine(bool asserted) { }
            public void SetNmiLine(bool asserted) { }
            private byte ReadBus(uint address) { _cycles++; return _bus.Read8(address); }
            private void HandleUndefinedOpcode(byte opcode) { _cycles++; }
        }
        """;

    [Fact]
    public void Valid_spec_generates_a_cpu_class_that_compiles()
    {
        var result = GeneratorTestHost.Run(ValidSpecSource);

        Assert.Empty(result.AllErrors);
        var tree = Assert.Single(result.GeneratedTrees);
        Assert.EndsWith("Tiny6502Cpu.g.cs", tree.FilePath);
        Assert.Contains("partial class Tiny6502Cpu", result.GeneratedText);
    }

    [Fact]
    public void CpuName_named_argument_overrides_derived_name()
    {
        var source = ValidSpecSource
            .Replace("[CpuSpecification(\"test6502\")]",
                     "[CpuSpecification(\"test6502\", CpuName = \"WeirdName\")]")
            .Replace("Tiny6502Cpu", "WeirdName");

        var result = GeneratorTestHost.Run(source);

        Assert.Empty(result.AllErrors);
        Assert.Contains("partial class WeirdName", result.GeneratedText);
    }

    [Fact]
    public void Class_without_attribute_generates_nothing()
    {
        var result = GeneratorTestHost.Run("namespace N; public static class NotASpec { }");

        Assert.Empty(result.GeneratedTrees);
        Assert.Empty(result.GeneratorDiagnostics);
    }
}
```

- [ ] **Step 3: Run to verify failure** — `dotnet test --filter "FullyQualifiedName~GeneratorHappyPathTests"` — FAIL (CS0246 `CpuSpecGenerator` not found).

- [ ] **Step 4: Implement the generator skeleton**

`src/CpuEmulator.Generators/SpecModel.cs`:

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace CpuEmulator.Generators;

internal sealed record SpecModel(
    string Namespace,
    string CpuName,
    string Architecture,
    ImmutableArray<RegisterModel> Registers,
    ImmutableArray<InstructionModel> Instructions);

internal sealed record RegisterModel(string Name, int Bits, string Role);

internal sealed record InstructionModel(byte Opcode, string Mnemonic, string Mode, ImmutableArray<OpModel> Ops);

internal sealed record OpModel(string Kind, ImmutableArray<string> Args);

/// <summary>Parser output: a model (null when errors prevented one) plus diagnostics.</summary>
internal sealed record ParsedSpec(SpecModel? Model, ImmutableArray<Diagnostic> Diagnostics);
```

`src/CpuEmulator.Generators/SpecDiagnostics.cs`:

```csharp
using Microsoft.CodeAnalysis;

namespace CpuEmulator.Generators;

internal static class SpecDiagnostics
{
    private const string Category = "CpuEmulator.Spec";

    private static DiagnosticDescriptor Make(string id, string title, string message) =>
        new(id, title, message, Category, DiagnosticSeverity.Error, isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor MissingRegisters = Make(
        "CPUGEN001", "Missing Registers table",
        "Spec class '{0}' must declare a field 'Registers' initialized with a collection expression of RegisterDef entries");

    public static readonly DiagnosticDescriptor InvalidRegister = Make(
        "CPUGEN002", "Invalid register definition",
        "Register entry '{0}' is not analyzable: {1}");

    public static readonly DiagnosticDescriptor MissingInstructions = Make(
        "CPUGEN003", "Missing Instructions table",
        "Spec class '{0}' must declare a field 'Instructions' initialized with a collection expression of Insn(...) entries");

    public static readonly DiagnosticDescriptor InvalidInstruction = Make(
        "CPUGEN004", "Invalid instruction definition",
        "Instruction entry '{0}' is not analyzable: {1}");

    public static readonly DiagnosticDescriptor DuplicateOpcode = Make(
        "CPUGEN005", "Duplicate opcode",
        "Opcode 0x{0} is defined more than once");

    public static readonly DiagnosticDescriptor UnknownMicroOp = Make(
        "CPUGEN006", "Unknown micro-op",
        "'{0}' is not a recognized micro-op factory (allowed: Load, Store, Transfer, Increment, SetNZ, Jump, BranchIf)");

    public static readonly DiagnosticDescriptor RoleViolation = Make(
        "CPUGEN007", "Register role violation",
        "{0}");
}
```

`src/CpuEmulator.Generators/SpecParser.cs` (Task-3 skeleton — full table parsing arrives in Tasks 4–5):

```csharp
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CpuEmulator.Generators;

internal static class SpecParser
{
    public static ParsedSpec Parse(GeneratorAttributeSyntaxContext context)
    {
        var classDecl = (ClassDeclarationSyntax)context.TargetNode;
        string ns = context.TargetSymbol.ContainingNamespace.ToDisplayString();
        string specName = classDecl.Identifier.Text;

        var attribute = context.Attributes[0];
        string architecture = attribute.ConstructorArguments.Length > 0
            ? attribute.ConstructorArguments[0].Value as string ?? "unknown"
            : "unknown";

        string cpuName = specName.EndsWith("Spec", System.StringComparison.Ordinal)
            ? specName.Substring(0, specName.Length - 4) + "Cpu"
            : specName + "Cpu";
        foreach (var named in attribute.NamedArguments)
        {
            if (named.Key == "CpuName" && named.Value.Value is string explicitName)
                cpuName = explicitName;
        }

        var model = new SpecModel(ns, cpuName, architecture,
            ImmutableArray<RegisterModel>.Empty, ImmutableArray<InstructionModel>.Empty);
        return new ParsedSpec(model, ImmutableArray<Diagnostic>.Empty);
    }
}
```

`src/CpuEmulator.Generators/CpuEmitter.cs` (Task-3 skeleton):

```csharp
using System.Text;

namespace CpuEmulator.Generators;

internal static class CpuEmitter
{
    public static string Emit(SpecModel model)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("// Generated by CpuEmulator.Generators from the CPU specification.");
        sb.AppendLine("// The hand-written partial MUST provide: a constructor, Reset(),");
        sb.AppendLine("// SetIrqLine(bool), SetNmiLine(bool), byte ReadBus(uint address)");
        sb.AppendLine("// (which increments _cycles), and HandleUndefinedOpcode(byte opcode).");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {model.Namespace};");
        sb.AppendLine();
        sb.AppendLine($"public sealed partial class {model.CpuName} : CpuEmulator.Core.ICpuCore");
        sb.AppendLine("{");
        sb.AppendLine($"    public string Architecture => \"{model.Architecture}\";");
        sb.AppendLine();
        sb.AppendLine("    internal long _cycles;");
        sb.AppendLine("    public long CycleCount => _cycles;");
        EmitBody(sb, model);   // Tasks 4-5 grow this
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitBody(StringBuilder sb, SpecModel model)
    {
        // Grown in Tasks 4 (registers/introspection) and 5 (Step/Run/Execute).
    }
}
```

`src/CpuEmulator.Generators/CpuSpecGenerator.cs`:

```csharp
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CpuEmulator.Generators;

[Generator]
public sealed class CpuSpecGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var specs = context.SyntaxProvider.ForAttributeWithMetadataName(
            "CpuEmulator.Core.Specification.CpuSpecificationAttribute",
            predicate: static (node, _) => node is ClassDeclarationSyntax,
            transform: static (ctx, _) => SpecParser.Parse(ctx));

        context.RegisterSourceOutput(specs, static (spc, parsed) =>
        {
            foreach (var diagnostic in parsed.Diagnostics)
                spc.ReportDiagnostic(diagnostic);
            if (parsed.Model is { } model)
                spc.AddSource($"{model.CpuName}.g.cs", CpuEmitter.Emit(model));
        });
    }
}
```

Note: the Task-3 generated class does NOT yet satisfy `ICpuCore` (no registers/Step), so `Valid_spec_generates_a_cpu_class_that_compiles` will report compilation errors for unimplemented members. For THIS task only, assert the weaker conditions (generated tree exists, class name right, no generator diagnostics) and mark the `Assert.Empty(result.AllErrors)` line with `// strengthened in Task 4` — Task 4 makes it real. Write the test with `Assert.Empty(result.GeneratorDiagnostics)` now and switch to `AllErrors` in Task 4.

- [ ] **Step 5: Run tests** — the three happy-path tests pass (with the weaker assertion noted above).

- [ ] **Step 6: Commit** — `feat: add incremental generator skeleton with in-memory test harness`

---

### Task 4: Register-table parsing + state/introspection emission

**Files:**
- Modify: `src/CpuEmulator.Generators/SpecParser.cs`, `CpuEmitter.cs`
- Create: `tests/CpuEmulator.Tests/Generators/RegisterParsingTests.cs`
- Modify: `tests/CpuEmulator.Tests/Generators/GeneratorHappyPathTests.cs` (strengthen to `AllErrors`)

- [ ] **Step 1: Write the failing tests**

`tests/CpuEmulator.Tests/Generators/RegisterParsingTests.cs`:

```csharp
namespace CpuEmulator.Tests.Generators;

public class RegisterParsingTests
{
    private static string WithRegisters(string registersBody) =>
        GeneratorHappyPathTests.ValidSpecSource.Replace(
            """
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 8),
                    new("X", 8),
                    new("S", 8, RegisterRole.StackPointer),
                    new("P", 8, RegisterRole.Status),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """,
            registersBody);

    [Fact]
    public void Registers_emit_fields_and_names_in_table_order()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("public byte A;", result.GeneratedText);
        Assert.Contains("public ushort PC;", result.GeneratedText);
        Assert.Contains("""["A", "X", "S", "P", "PC"]""", result.GeneratedText);
    }

    [Fact]
    public void Missing_registers_field_reports_CPUGEN001()
    {
        var result = GeneratorTestHost.Run(WithRegisters(""));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN001");
        Assert.Empty(result.GeneratedTrees); // no model -> nothing generated
    }

    [Fact]
    public void Non_literal_register_entry_reports_CPUGEN002()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new(System.Environment.MachineName, 8),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
    }

    [Fact]
    public void Unsupported_register_width_reports_CPUGEN002()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 12),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
    }

    [Fact]
    public void Missing_program_counter_reports_CPUGEN007()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 8),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN007");
    }

    [Fact]
    public void Two_program_counters_report_CPUGEN007()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("PC", 16, RegisterRole.ProgramCounter),
                    new("PC2", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN007");
    }

    [Fact]
    public void Duplicate_register_name_reports_CPUGEN002()
    {
        var result = GeneratorTestHost.Run(WithRegisters("""
                public static readonly RegisterDef[] Registers =
                [
                    new("A", 8),
                    new("A", 8),
                    new("PC", 16, RegisterRole.ProgramCounter),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN002");
    }
}
```

- [ ] **Step 2: Run to verify failure** — the new tests fail (no parsing yet; CPUGEN001 never reported, no fields emitted).

- [ ] **Step 3: Implement register parsing in `SpecParser`**

Replace the model construction in `SpecParser.Parse` with full register parsing. Add to `SpecParser.cs`:

```csharp
    private static ImmutableArray<RegisterModel> ParseRegisters(
        ClassDeclarationSyntax classDecl,
        string specName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Registers");
        if (field?.Declaration.Variables[0].Initializer?.Value is not CollectionExpressionSyntax collection)
        {
            diagnostics.Add(Diagnostic.Create(
                SpecDiagnostics.MissingRegisters, classDecl.Identifier.GetLocation(), specName));
            return ImmutableArray<RegisterModel>.Empty;
        }

        var registers = ImmutableArray.CreateBuilder<RegisterModel>();
        var seenNames = new HashSet<string>(System.StringComparer.Ordinal);

        foreach (var element in collection.Elements)
        {
            if (element is not ExpressionElementSyntax expr ||
                GetCreationArguments(expr.Expression) is not { } args ||
                args.Count is < 2 or > 3 ||
                LiteralString(args[0]) is not { } name ||
                LiteralInt(args[1]) is not { } bits)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), element.ToString(),
                    "expected new(\"NAME\", bits[, RegisterRole.X]) with literal arguments"));
                continue;
            }

            if (bits is not (8 or 16))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "register width must be 8 or 16 bits"));
                continue;
            }

            if (!seenNames.Add(name))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                    element.GetLocation(), name, "duplicate register name"));
                continue;
            }

            string role = "General";
            if (args.Count == 3)
            {
                if (EnumMemberName(args[2], "RegisterRole") is not { } parsedRole)
                {
                    diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidRegister,
                        element.GetLocation(), name, "third argument must be a RegisterRole member"));
                    continue;
                }
                role = parsedRole;
            }

            registers.Add(new RegisterModel(name, bits, role));
        }

        int pcCount = registers.Count(r => r.Role == "ProgramCounter");
        if (pcCount != 1)
            diagnostics.Add(Diagnostic.Create(SpecDiagnostics.RoleViolation,
                classDecl.Identifier.GetLocation(),
                $"spec must declare exactly one ProgramCounter register (found {pcCount})"));
        if (registers.Count(r => r.Role == "Status") > 1)
            diagnostics.Add(Diagnostic.Create(SpecDiagnostics.RoleViolation,
                classDecl.Identifier.GetLocation(), "spec declares more than one Status register"));

        return registers.ToImmutable();
    }

    private static FieldDeclarationSyntax? FindArrayField(ClassDeclarationSyntax classDecl, string name) =>
        classDecl.Members.OfType<FieldDeclarationSyntax>()
            .FirstOrDefault(f => f.Declaration.Variables.Count == 1 &&
                                 f.Declaration.Variables[0].Identifier.Text == name);

    /// <summary>Arguments of new(...) / new T(...) / Factory(...); null when not a creation/invocation.</summary>
    private static IReadOnlyList<ExpressionSyntax>? GetCreationArguments(ExpressionSyntax expression) =>
        expression switch
        {
            ImplicitObjectCreationExpressionSyntax c => c.ArgumentList.Arguments.Select(a => a.Expression).ToList(),
            ObjectCreationExpressionSyntax c => c.ArgumentList?.Arguments.Select(a => a.Expression).ToList(),
            InvocationExpressionSyntax i => i.ArgumentList.Arguments.Select(a => a.Expression).ToList(),
            _ => null,
        };

    private static string? LiteralString(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: string s } ? s : null;

    private static int? LiteralInt(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: int i } ? i : null;

    /// <summary>For 'EnumType.Member' returns "Member" when the type name matches.</summary>
    private static string? EnumMemberName(ExpressionSyntax expression, string enumTypeName) =>
        expression is MemberAccessExpressionSyntax
        {
            Expression: IdentifierNameSyntax type,
            Name: IdentifierNameSyntax member,
        } && type.Identifier.Text == enumTypeName
            ? member.Identifier.Text
            : null;
```

Wire into `Parse`: build a `diagnostics` builder; call `ParseRegisters`; if any diagnostic with `Severity == Error` exists, return `new ParsedSpec(null, diagnostics.ToImmutable())`, else build the model with the parsed registers (Instructions still empty until Task 5 — keep `ImmutableArray<InstructionModel>.Empty`).

Required usings in `SpecParser.cs`: `System.Collections.Generic`, `System.Collections.Immutable`, `System.Linq`, `Microsoft.CodeAnalysis`, `Microsoft.CodeAnalysis.CSharp.Syntax`.

- [ ] **Step 4: Implement state + introspection emission in `CpuEmitter.EmitBody`**

```csharp
    private static void EmitBody(StringBuilder sb, SpecModel model)
    {
        sb.AppendLine();
        foreach (var register in model.Registers)
            sb.AppendLine($"    public {(register.Bits == 8 ? "byte" : "ushort")} {register.Name};");

        string nameList = string.Join(", ", model.Registers.Select(r => $"\"{r.Name}\""));
        sb.AppendLine();
        sb.AppendLine($"    private static readonly string[] s_registerNames = [{nameList}];");
        sb.AppendLine("    public System.Collections.Generic.IReadOnlyList<string> RegisterNames => s_registerNames;");

        sb.AppendLine();
        sb.AppendLine("    public ulong GetRegister(string name) => name switch");
        sb.AppendLine("    {");
        foreach (var register in model.Registers)
            sb.AppendLine($"        \"{register.Name}\" => {register.Name},");
        sb.AppendLine("        _ => throw new System.ArgumentException($\"Unknown register '{name}'.\", nameof(name)),");
        sb.AppendLine("    };");

        sb.AppendLine();
        sb.AppendLine("    public void SetRegister(string name, ulong value)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (name)");
        sb.AppendLine("        {");
        foreach (var register in model.Registers)
        {
            string cast = register.Bits == 8 ? "byte" : "ushort";
            sb.AppendLine($"            case \"{register.Name}\": {register.Name} = ({cast})value; break;");
        }
        sb.AppendLine("            default: throw new System.ArgumentException($\"Unknown register '{name}'.\", nameof(name));");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        EmitExecution(sb, model);   // Task 5
    }

    private static void EmitExecution(StringBuilder sb, SpecModel model)
    {
        // Task 5: Step / Run / Execute.
    }
```

- [ ] **Step 5: Strengthen the happy-path test.** In `GeneratorHappyPathTests.Valid_spec_generates_a_cpu_class_that_compiles`, the generated class still lacks `Step`/`Run` (`ICpuCore` unsatisfied), so keep `Assert.Empty(result.GeneratorDiagnostics)` for now; the switch to `Assert.Empty(result.AllErrors)` happens in Task 5 when the interface is complete. (Register tests above use `AllErrors` only in the first test — adjust it the same way: use `GeneratorDiagnostics` now, flip both in Task 5. Add `// CS gap closes in Task 5` comments.)

- [ ] **Step 6: Run tests** — register tests pass; full suite green.

- [ ] **Step 7: Commit** — `feat: parse register tables and emit state fields with introspection`

---

### Task 5: Instruction-table parsing + Step/Run/Execute emission

**Files:**
- Modify: `src/CpuEmulator.Generators/SpecParser.cs`, `CpuEmitter.cs`
- Create: `tests/CpuEmulator.Tests/Generators/InstructionParsingTests.cs`
- Modify: happy-path/register tests (flip to `AllErrors` — the interface is complete after this task)

- [ ] **Step 1: Write the failing tests**

`tests/CpuEmulator.Tests/Generators/InstructionParsingTests.cs`:

```csharp
namespace CpuEmulator.Tests.Generators;

public class InstructionParsingTests
{
    private static string WithInstructions(string instructionsBody) =>
        GeneratorHappyPathTests.ValidSpecSource.Replace(
            """
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
                    Insn(0xEA, "NOP", AddrMode.Implied, []),
                ];
            """,
            instructionsBody);

    [Fact]
    public void Valid_spec_compiles_and_implements_ICpuCore()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        Assert.Empty(result.AllErrors);
        Assert.Contains("public void Step()", result.GeneratedText);
        Assert.Contains("public void Run(ref long cycleBudget)", result.GeneratedText);
    }

    [Fact]
    public void Instruction_table_is_summarized_in_generated_output()
    {
        var result = GeneratorTestHost.Run(GeneratorHappyPathTests.ValidSpecSource);

        // The summary comment is review/debug aid AND pins that parsing saw both rows.
        Assert.Contains("0xA9 LDA Immediate", result.GeneratedText);
        Assert.Contains("0xEA NOP Implied", result.GeneratedText);
    }

    [Fact]
    public void Missing_instructions_field_reports_CPUGEN003()
    {
        var result = GeneratorTestHost.Run(WithInstructions(""));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN003");
    }

    [Fact]
    public void Duplicate_opcode_reports_CPUGEN005()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A)]),
                    Insn(0xA9, "LDA", AddrMode.ZeroPage, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN005");
    }

    [Fact]
    public void Unknown_micro_op_factory_reports_CPUGEN006()
    {
        // 'Frobnicate' type-checks nowhere, but the generator must report ITS diagnostic
        // (not just let the compile error stand) so spec authors get a spec-level message.
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", AddrMode.Immediate, [Frobnicate(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN006");
    }

    [Fact]
    public void Non_literal_opcode_reports_CPUGEN004()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                private static byte Op() => 0xA9;
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(Op(), "LDA", AddrMode.Immediate, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN004");
    }

    [Fact]
    public void Unknown_addressing_mode_reports_CPUGEN004()
    {
        var result = GeneratorTestHost.Run(WithInstructions("""
                public static readonly InstructionDef[] Instructions =
                [
                    Insn(0xA9, "LDA", (AddrMode)99, [Load(Reg.A)]),
                ];
            """));

        Assert.Contains(result.GeneratorDiagnostics, d => d.Id == "CPUGEN004");
    }
}
```

- [ ] **Step 2: Run to verify failure.**

- [ ] **Step 3: Implement instruction parsing.** Add to `SpecParser.cs` (and call from `Parse`, mirroring `ParseRegisters`):

```csharp
    private static readonly Dictionary<string, int> s_microOpArity = new(System.StringComparer.Ordinal)
    {
        ["Load"] = 1, ["Store"] = 1, ["Transfer"] = 2, ["Increment"] = 1,
        ["SetNZ"] = 1, ["Jump"] = 0, ["BranchIf"] = 2,
    };

    private static readonly HashSet<string> s_addrModes = new(System.StringComparer.Ordinal)
    {
        "Implied", "Immediate", "ZeroPage", "Absolute", "Relative",
    };

    private static ImmutableArray<InstructionModel> ParseInstructions(
        ClassDeclarationSyntax classDecl,
        string specName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var field = FindArrayField(classDecl, "Instructions");
        if (field?.Declaration.Variables[0].Initializer?.Value is not CollectionExpressionSyntax collection)
        {
            diagnostics.Add(Diagnostic.Create(
                SpecDiagnostics.MissingInstructions, classDecl.Identifier.GetLocation(), specName));
            return ImmutableArray<InstructionModel>.Empty;
        }

        var instructions = ImmutableArray.CreateBuilder<InstructionModel>();
        var seenOpcodes = new HashSet<int>();

        foreach (var element in collection.Elements)
        {
            if (element is not ExpressionElementSyntax { Expression: InvocationExpressionSyntax invocation } ||
                InvokedName(invocation) != "Insn" ||
                invocation.ArgumentList.Arguments.Count != 4)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "expected Insn(opcode, \"MNEMONIC\", AddrMode.X, [micro-ops])"));
                continue;
            }

            var args = invocation.ArgumentList.Arguments;
            int? opcode = LiteralInt(args[0].Expression);
            string? mnemonic = LiteralString(args[1].Expression);
            string? mode = EnumMemberName(args[2].Expression, "AddrMode");

            if (opcode is null || mnemonic is null)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "opcode and mnemonic must be literals"));
                continue;
            }
            if (mode is null || !s_addrModes.Contains(mode))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.InvalidInstruction,
                    element.GetLocation(), Truncate(element.ToString()),
                    "third argument must be a known AddrMode member"));
                continue;
            }
            if (opcode is < 0 or > 0xFF || !seenOpcodes.Add(opcode.Value))
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.DuplicateOpcode,
                    element.GetLocation(), opcode.Value.ToString("X2")));
                continue;
            }

            if (args[3].Expression is not CollectionExpressionSyntax opsCollection ||
                ParseOps(opsCollection, diagnostics) is not { } ops)
            {
                continue; // ParseOps reported the diagnostic
            }

            instructions.Add(new InstructionModel((byte)opcode.Value, mnemonic, mode, ops));
        }

        return instructions.ToImmutable();
    }

    private static ImmutableArray<OpModel>? ParseOps(
        CollectionExpressionSyntax collection,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var ops = ImmutableArray.CreateBuilder<OpModel>();
        foreach (var element in collection.Elements)
        {
            if (element is not ExpressionElementSyntax { Expression: InvocationExpressionSyntax invocation } ||
                InvokedName(invocation) is not { } kind)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.UnknownMicroOp,
                    element.GetLocation(), Truncate(element.ToString())));
                return null;
            }

            if (!s_microOpArity.TryGetValue(kind, out int arity) ||
                invocation.ArgumentList.Arguments.Count != arity)
            {
                diagnostics.Add(Diagnostic.Create(SpecDiagnostics.UnknownMicroOp,
                    element.GetLocation(), kind));
                return null;
            }

            var opArgs = ImmutableArray.CreateBuilder<string>();
            foreach (var argument in invocation.ArgumentList.Arguments)
            {
                string? value =
                    EnumMemberName(argument.Expression, "Reg") ??
                    EnumMemberName(argument.Expression, "Flag") ??
                    BoolLiteral(argument.Expression);
                if (value is null)
                {
                    diagnostics.Add(Diagnostic.Create(SpecDiagnostics.UnknownMicroOp,
                        argument.GetLocation(), Truncate(argument.ToString())));
                    return null;
                }
                opArgs.Add(value);
            }

            ops.Add(new OpModel(kind, opArgs.ToImmutable()));
        }
        return ops.ToImmutable();
    }

    private static string? InvokedName(InvocationExpressionSyntax invocation) =>
        invocation.Expression switch
        {
            IdentifierNameSyntax id => id.Identifier.Text,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax id } => id.Identifier.Text,
            _ => null,
        };

    private static string? BoolLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: bool b } ? (b ? "true" : "false") : null;

    private static string Truncate(string text) =>
        text.Length <= 60 ? text : text.Substring(0, 57) + "...";
```

- [ ] **Step 4: Implement execution emission.** Replace `CpuEmitter.EmitExecution`:

```csharp
    private static void EmitExecution(StringBuilder sb, SpecModel model)
    {
        string pc = model.Registers.First(r => r.Role == "ProgramCounter").Name;

        sb.AppendLine();
        sb.AppendLine("    // Instruction table parsed from the spec (execution emitted in chunk 2b):");
        foreach (var instruction in model.Instructions)
            sb.AppendLine($"    //   0x{instruction.Opcode:X2} {instruction.Mnemonic} {instruction.Mode}");

        sb.AppendLine();
        sb.AppendLine("    /// <summary>Execute one instruction. Always advances CycleCount by at least one.</summary>");
        sb.AppendLine("    public void Step()");
        sb.AppendLine("    {");
        sb.AppendLine($"        byte opcode = ReadBus({pc});");
        sb.AppendLine($"        {pc}++;");
        sb.AppendLine("        Execute(opcode);");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine("    public void Run(ref long cycleBudget)");
        sb.AppendLine("    {");
        sb.AppendLine("        while (cycleBudget > 0)");
        sb.AppendLine("        {");
        sb.AppendLine("            long before = _cycles;");
        sb.AppendLine("            Step();");
        sb.AppendLine("            cycleBudget -= _cycles - before;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");

        sb.AppendLine();
        sb.AppendLine("    private void Execute(byte opcode)");
        sb.AppendLine("    {");
        sb.AppendLine("        switch (opcode)");
        sb.AppendLine("        {");
        sb.AppendLine("            // Per-opcode execution methods are emitted in chunk 2b.");
        sb.AppendLine("            default:");
        sb.AppendLine("                HandleUndefinedOpcode(opcode);");
        sb.AppendLine("                break;");
        sb.AppendLine("        }");
        sb.AppendLine("    }");
    }
```

- [ ] **Step 5: Flip the deferred assertions** in happy-path and register tests from `GeneratorDiagnostics` to `AllErrors` (the generated class now fully implements `ICpuCore` when paired with the hand-written partial in the test source). Remove the `// closes in Task N` comments.

- [ ] **Step 6: Run tests** — all generator tests pass; suite green.

- [ ] **Step 7: Commit** — `feat: parse instruction tables and emit Step/Run with undefined dispatch`

> **Post-review amendments (applied at Task 5):** CPUGEN008 added — micro-op register
> references are cross-checked against the Registers table at parse time instead of surfacing
> as CS0103 in generated code. Accepted risk, recorded: factory calls are matched by simple
> name only (no semantic-model binding check); within the constrained-DSL contract anything
> else is already a CPUGEN diagnostic, and a user-defined same-name helper in a spec class is
> considered out of contract for M1.

> **Post-quality-review hardenings (applied before Task 6):** spec mistakes that compile
> cleanly now surface as CPUGEN diagnostics, never as errors inside generated code — register
> and CpuName identifiers validated, Architecture emitted via FormatLiteral, CPUGEN009 added
> (global-namespace spec, invalid CpuName), hint names namespace-qualified, CPUGEN008 points
> at the offending argument, out-of-range opcodes are CPUGEN004 (not "duplicate"), known-factory
> arity mismatches get a specific message, and emitted truncation casts + the PC increment are
> `unchecked(...)` (correct under consumer CheckForOverflowUnderflow). Tripwire for chunk 2b:
> convert pipeline Diagnostics to an equatable DiagnosticInfo BEFORE any `Combine` is added to
> the incremental pipeline. Second 2b carry-forward: mnemonic strings currently flow into a generated comment unsanitized — validate them when 2b turns mnemonics into emitted identifiers/disassembler strings.

---

### Task 6: The real Mos6502 — spec table, hand-written partial, behavioral tests

**Files:**
- Create: `src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs`
- Create: `src/CpuEmulator.Cpus.Mos6502/Mos6502Cpu.cs`
- Test: `tests/CpuEmulator.Tests/Mos6502/Mos6502SkeletonTests.cs`

- [ ] **Step 1: Write the spec table**

`src/CpuEmulator.Cpus.Mos6502/Mos6502Spec.cs`:

```csharp
using CpuEmulator.Core.Specification;
using static CpuEmulator.Core.Specification.Spec;

namespace CpuEmulator.Cpus.Mos6502;

/// <summary>The MOS 6502 specification. Chunk 2 carries an 11-opcode proving subset;
/// chunk 3 scales to the full documented instruction set.</summary>
[CpuSpecification("mos6502")]
public static class Mos6502Spec
{
    public static readonly RegisterDef[] Registers =
    [
        new("A", 8),
        new("X", 8),
        new("Y", 8),
        new("S", 8, RegisterRole.StackPointer),
        new("P", 8, RegisterRole.Status),
        new("PC", 16, RegisterRole.ProgramCounter),
    ];

    public static readonly InstructionDef[] Instructions =
    [
        Insn(0xA9, "LDA", AddrMode.Immediate, [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xA5, "LDA", AddrMode.ZeroPage,  [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0xAD, "LDA", AddrMode.Absolute,  [Load(Reg.A), SetNZ(Reg.A)]),
        Insn(0x85, "STA", AddrMode.ZeroPage,  [Store(Reg.A)]),
        Insn(0x8D, "STA", AddrMode.Absolute,  [Store(Reg.A)]),
        Insn(0xA2, "LDX", AddrMode.Immediate, [Load(Reg.X), SetNZ(Reg.X)]),
        Insn(0xAA, "TAX", AddrMode.Implied,   [Transfer(Reg.A, Reg.X), SetNZ(Reg.X)]),
        Insn(0xE8, "INX", AddrMode.Implied,   [Increment(Reg.X), SetNZ(Reg.X)]),
        Insn(0x4C, "JMP", AddrMode.Absolute,  [Jump()]),
        Insn(0xD0, "BNE", AddrMode.Relative,  [BranchIf(Flag.Z, false)]),
        Insn(0xEA, "NOP", AddrMode.Implied,   []),
    ];
}
```

- [ ] **Step 2: Write the hand-written partial**

`src/CpuEmulator.Cpus.Mos6502/Mos6502Cpu.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Core.Specification;

namespace CpuEmulator.Cpus.Mos6502;

/// <summary>Hand-written half of the 6502: bus wiring, reset, undefined-opcode policy, and
/// interrupt-line recording. The generated half (see obj/generated/) owns state,
/// introspection, and the Step/Run/Execute pipeline.</summary>
public sealed partial class Mos6502Cpu
{
    private readonly IAddressSpace _bus;
    private readonly UndefinedOpcodePolicy _undefinedPolicy;
    private bool _irqLine;
    private bool _nmiLine;

    public Mos6502Cpu(IAddressSpace bus, UndefinedOpcodePolicy undefinedPolicy = UndefinedOpcodePolicy.Throw)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _bus = bus;
        _undefinedPolicy = undefinedPolicy;
    }

    /// <summary>6502 reset: load PC from the vector at $FFFC/$FFFD, S = $FD, I set.
    /// Costs the authentic 7 cycles, charged coarsely (2 vector reads + 5 internal) —
    /// per-cycle reset bus activity is a documented M1 deviation.</summary>
    public void Reset()
    {
        byte lo = ReadBus(0xFFFC);
        byte hi = ReadBus(0xFFFD);
        PC = (ushort)(lo | (hi << 8));
        S = 0xFD;
        P = 0x34; // I + bits 5,4: stored P models the phantom B/unused bits as set (NESdev
                  // power-up convention); chunk 3's PHP/PLP/BRK logic owns the bit-4/5
                  // push/pull conventions.
        _cycles += 5;
    }

    /// <summary>Lines are recorded but not yet serviced — the interrupt sequence lands in
    /// chunk 3 with the full instruction set.</summary>
    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    public void SetNmiLine(bool asserted) => _nmiLine = asserted;

    private byte ReadBus(uint address)
    {
        _cycles++;
        return _bus.Read8(address);
    }

    private void WriteBus(uint address, byte value)
    {
        _cycles++;
        _bus.Write8(address, value);
    }

    private void HandleUndefinedOpcode(byte opcode)
    {
        if (_undefinedPolicy == UndefinedOpcodePolicy.Nop)
        {
            _cycles++; // 2-cycle NOP total: opcode fetch + one internal cycle
            return;
        }
        throw new UndefinedOpcodeException(opcode, (uint)((PC - 1) & 0xFFFF));
    }
}
```

(`WriteBus` is unused until chunk 2b — if the unused-member warning fails the build under warnings-as-errors, delete it here and let chunk 2b reintroduce it; note which way it went in your report. The C# compiler does not warn for unused private *methods*, so it should be fine as-is.)

- [ ] **Step 3: Write the behavioral tests**

`tests/CpuEmulator.Tests/Mos6502/Mos6502SkeletonTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Core.Specification;
using CpuEmulator.Cpus.Mos6502;

namespace CpuEmulator.Tests.Mos6502;

public class Mos6502SkeletonTests
{
    private static (Mos6502Cpu Cpu, AddressSpace Space) NewCpu(
        UndefinedOpcodePolicy policy = UndefinedOpcodePolicy.Throw)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        return (new Mos6502Cpu(space, policy), space);
    }

    [Fact]
    public void Architecture_and_register_names_come_from_the_spec()
    {
        var (cpu, _) = NewCpu();

        Assert.Equal("mos6502", cpu.Architecture);
        Assert.Equal(["A", "X", "Y", "S", "P", "PC"], cpu.RegisterNames);
    }

    [Fact]
    public void Set_and_get_round_trip_with_width_truncation()
    {
        var (cpu, _) = NewCpu();

        cpu.SetRegister("A", 0x1FF);       // truncates to 8 bits
        cpu.SetRegister("PC", 0x1_8000);   // truncates to 16 bits

        Assert.Equal(0xFFul, cpu.GetRegister("A"));
        Assert.Equal(0x8000ul, cpu.GetRegister("PC"));
    }

    [Fact]
    public void Unknown_register_name_throws_ArgumentException()
    {
        var (cpu, _) = NewCpu();

        Assert.Throws<ArgumentException>(() => cpu.GetRegister("Q"));
        Assert.Throws<ArgumentException>(() => cpu.SetRegister("Q", 1));
    }

    [Fact]
    public void Reset_loads_the_vector_and_costs_seven_cycles()
    {
        var (cpu, space) = NewCpu();
        space.Write8(0xFFFC, 0x00);
        space.Write8(0xFFFD, 0x80);

        cpu.Reset();

        Assert.Equal(0x8000ul, cpu.GetRegister("PC"));
        Assert.Equal(0xFDul, cpu.GetRegister("S"));
        Assert.Equal(0x34ul, cpu.GetRegister("P"));
        Assert.Equal(7, cpu.CycleCount);
    }

    [Fact]
    public void Undefined_opcode_with_throw_policy_reports_opcode_and_address()
    {
        var (cpu, space) = NewCpu();
        space.Write8(0x0200, 0xFF);        // 0xFF is not in the subset
        cpu.SetRegister("PC", 0x0200);

        var ex = Assert.Throws<UndefinedOpcodeException>(cpu.Step);

        Assert.Equal(0xFF, ex.Opcode);
        Assert.Equal(0x0200u, ex.Address);
    }

    [Fact]
    public void Undefined_opcode_with_nop_policy_advances_two_cycles()
    {
        var (cpu, space) = NewCpu(UndefinedOpcodePolicy.Nop);
        space.Write8(0x0000, 0x02); // 0x02 is a JAM slot — permanently undefined

        cpu.Step();

        Assert.Equal(2, cpu.CycleCount);
        Assert.Equal(0x0001ul, cpu.GetRegister("PC"));
    }

    [Fact]
    public void Run_consumes_budget_in_two_cycle_undefined_nops()
    {
        var (cpu, space) = NewCpu(UndefinedOpcodePolicy.Nop);
        for (uint address = 0; address < 8; address++)
            space.Write8(address, 0x02); // JAM slots — permanently undefined

        long budget = 10;
        cpu.Run(ref budget);

        Assert.Equal(0, budget);
        Assert.Equal(10, cpu.CycleCount);   // 5 steps × 2 cycles
    }

    [Fact]
    public void Cpu_composes_with_Machine_through_the_builder()
    {
        var machine = Machine.Create("breadboard")
            .WithAddressSpace(AddressSpaceKind.Program, 16)
            .WithRam(AddressSpaceKind.Program, 0x0000, 0x10000)
            .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program),
                                           UndefinedOpcodePolicy.Nop))
            .Build();
        machine.Space(AddressSpaceKind.Program).Write8(0xFFFC, 0x00);
        machine.Space(AddressSpaceKind.Program).Write8(0xFFFD, 0x02);
        for (uint address = 0x0200; address < 0x0214; address++)
            machine.Space(AddressSpaceKind.Program).Write8(address, 0x02); // JAM slots

        machine.Reset();
        long executed = machine.Run(20);

        Assert.Equal(20, executed);
        Assert.Equal(0x0200ul + 10, machine.Cpu.GetRegister("PC")); // 10 two-cycle NOP-policy steps
    }
}
```

- [ ] **Step 4: Run tests** — `dotnet test --filter "FullyQualifiedName~Mos6502SkeletonTests"` — PASS (8 tests). Any failure means the generator emitted wrong code — fix the EMITTER (or parser), never the test, and re-run the full suite (generator tests catch regressions).

Note on `Cpu_composes_with_Machine_through_the_builder`: `machine.Reset()` costs 7 CPU cycles *before* `Run(20)` — `Machine.Run` budgets from the CPU's current count, so 20 budget = 10 NOP-policy steps exactly; PC advances 10.

- [ ] **Step 5: Run the FULL suite** — everything green, zero warnings.

- [ ] **Step 6: Commit** — `feat: add Mos6502 spec table and hand-written CPU partial, generated end-to-end`

> **Post-review carry-forwards from Task 6 (for 2b/3):** add `WriteBus(uint, byte)` (which
> increments `_cycles`) to the generated seam-contract header when 2b extends the emitter;
> make generated `_cycles` private in 2b's emitter pass (partials share privates); document
> the trace convention (reads charge `_cycles` before the bus access) before the recording
> bus / TomHarte differ hard-codes one; chunk 3 must define Reset's effect on latched
> IRQ/NMI line state when servicing lands, and note second-reset silicon semantics (S-=3,
> I-only) remain coarsely modeled.

---

### Task 7: Docs, verification, push, PR

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Full verification** — `dotnet build --no-incremental` (0 warnings) && `dotnet test` (all green). Also inspect `src/CpuEmulator.Cpus.Mos6502/obj/generated/` and confirm the generated `Mos6502Cpu.g.cs` is present and readable.

- [ ] **Step 2: Update README status section** — replace the "Next" sentence in `## Status` so it reads:

```markdown
Milestone 1 in progress. `CpuEmulator.Core` (contracts) and the Roslyn source-generator
front-end are implemented and tested: CPU specs are typed C# tables, parsed with build-time
diagnostics, generating state/introspection/dispatch for `Mos6502Cpu`. Next: per-opcode
interpreter emission (chunk 2b), then the full 6502 + SingleStepTests validation.
```

- [ ] **Step 3: Commit** — `docs: note generator front-end status in README`

- [ ] **Step 4: Push and open the PR** (do not merge):

```bash
git push -u origin feat/m1-generator-frontend
gh pr create --title "M1 chunk 2a: spec DSL + source-generator front-end" --body "..."
```

(The controller supplies the final PR body after the pre-push whole-branch review.)

---

## Plan self-review (completed at write time)

- **Spec coverage (§5, §7):** constrained DSL-in-C# with named factories ✓ (Task 2); "fail at build" via CPUGEN001–007 diagnostics ✓ (Tasks 4–5); one-spec→generated-artifacts pipeline proven for artifacts ① (state struct) and the Step/Run skeleton ✓; artifacts ② (per-opcode interpreter bodies) and ④ (disassembler) are chunk 2b; artifact ③ (IL emitters) is chunk 3, generate-only — declared in the header. Undefined-opcode policy (§7) ✓ Throw/Nop with callback deferred.
- **Placeholder scan:** every code step is complete; the two intentionally-staged assertions (`GeneratorDiagnostics` → `AllErrors`) are explicit with flip instructions in Task 5; `EmitBody`/`EmitExecution` grow across tasks by design with full code at each stage.
- **Type consistency:** `ReadBus(uint)`/`HandleUndefinedOpcode(byte)`/`_cycles` names match across emitter output, hand-written partial, and the test-source partial; `RegisterModel.Role` strings match `RegisterRole` member names; `UndefinedOpcodeException(byte, uint)` matches both throw site and test assertions; `Reg`/`Flag`/`AddrMode` member sets match parser whitelists.
- **Known risk, accepted:** Roslyn generator code written blind tends to have small compile-time errors (e.g., collection-expression syntax node shapes). The TDD gates exist precisely to surface these; implementers fix the generator, never weaken tests, and report deviations.
