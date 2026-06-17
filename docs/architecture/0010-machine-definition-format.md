# ADR 0010 — The machine-definition format (config-over-code: declarative manifest + code peripherals)

> **Status:** Proposed (drafted by Claude Architect; awaiting owner sign-off). No implementation now — this ADR
> formalizes the **config-over-code** layer for the emulated-computer arc, decided HYBRID by the owner: a declarative
> **JSON manifest** defines the machine's *wiring/composition* (CPU choice, memory map, device instances, interrupt
> wiring, clock/refresh, per-device timing tier), while device *behavior* stays **code** (`IPeripheral` components
> resolved through a registry). It is authored ahead of the loader's implementation so SP0's contracts land
> **config-ready** (clean component boundaries + a registry seam), even though the loader itself does not arrive until
> **SP1**.
> **Date:** 2026-06-17
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Supersedes / relates to:**
> - **ADR 0009** (`0009-device-jit-contract-and-peripheral-design.md`) — the device↔JIT contract. The manifest MUST be
>   consistent with all three of its decisions: a region's **fastmem-RAM vs MMIO** classification (Decision 1), the
>   **bank-switch/remap** points it can declare statically vs. what stays a code "mapper" (Decision 2 — the
>   block-invalidation signal), and the per-device **`TimingTier`** (Decision 3). The manifest is the *declarative
>   surface* over the seams ADR 0009 defined; it does not add new runtime behavior.
> - **The SP0 foundation design** (`docs/superpowers/specs/2026-06-17-emulated-computer-sp0-foundation-design.md`) — the
>   one hand-coded `DemoMachine`. SP0 builds **no loader** (YAGNI for one machine); its job for this ADR is to land the
>   device contracts so they are config-shaped (a chip = `IPeripheral` + capability interfaces + a small param surface),
>   which a manifest can later instantiate by id.
> - **ADR 0002** (`0002-address-space-scaling.md`) — the flat 256-byte-page table, the 8..24-bit `addressBits` bound,
>   and the page-aligned / page-multiple mapping rule (`AddressSpace.ValidateRange`) the manifest's memory map inherits
>   verbatim.
> - **The CPU spec→generator pipeline** (`tools/CpuEmulator.SpecImporter/data/*.json` + `src/CpuEmulator.Generators/`)
>   — the project's spec-driven DNA and the **`CPUGEN0NN` diagnostic discipline** (`SpecDiagnostics.cs`) the manifest
>   loader's validation deliberately mirrors (a `MACHGEN0NN` analogue).

---

## 1. Context

### 1.1 The composition seam today is hand-coded

A machine is composed *in C#* through a fluent builder. The canonical example, `Breadboard6502`
(`src/CpuEmulator.Host/Breadboard6502.cs`), is 12 lines of `MachineBuilder` calls:

```csharp
Machine = Machine.Create("breadboard6502")
    .WithAddressSpace(AddressSpaceKind.Program, addressBits: 16)
    .WithRam(AddressSpaceKind.Program, 0x0000, 0xD000)              // RAM $0000–$CFFF (52 KiB)
    .WithPeripheral(AddressSpaceKind.Program, UartBase, 0x0100, Uart)   // UART $D000–$D0FF
    .WithPeripheral(AddressSpaceKind.Program, TimerBase, 0x0100, Timer) // Timer $D100–$D1FF
    .WithRom(AddressSpaceKind.Program, 0xE000, DemoRom.Build())    // ROM $E000–$FFFF (8 KiB)
    .WithCpu(ctx => new Mos6502Cpu(ctx.Space(AddressSpaceKind.Program)))
    .Build();
```

Every fact in that block is *wiring*: which CPU, which address-space width, where RAM/ROM/MMIO sit, which device object
is mapped at which base, how big its window is. `MachineBuilder.Build()` (`MachineBuilder.cs:51`) constructs a `Machine`
(`Machine.cs:23`) in the two-phase discipline ADR 0009 relies on — phase 1 maps all memory, phase 2 builds the CPU
(capturing spaces), phase 3 maps peripherals then `Realize`s them. The CPU factory and the device *objects* are the
only *behavior* in the block; everything else is declarative data expressed as imperative calls.

### 1.2 Why a config layer, and why now

The arc's whole premise (SP0 → SP1 Atari 800 → SP3 PC clone, SP0 §1) is a **composable, CPU-agnostic machine toolkit**
proven by *multiple* machines. The moment there is a second machine, and especially the moment there are machine
**variants** (Atari 800 vs 800XL vs 130XE; IBM PC vs PC/XT), the hand-coded path forces a near-duplicate C# class per
variant whose only differences are a few numbers (RAM size, a ROM file, one extra device). That is exactly the kind of
"copy a 200-line class, change three constants, forget to change the fourth" surface where config-over-code earns its
keep: a variant becomes a small data diff, not a new compiled type, and the diff is reviewable as data.

But — and this is the owner's HYBRID call — **most of what makes a machine hard is behavior, not wiring.** A C64 PLA, an
Atari cartridge mapper, an Apple II soft-switch bank, an ANTIC display list: these are *algorithms over the bus*, not
tables. Trying to express them in config means inventing a Turing-complete DSL, which is a second programming language
to design, validate, debug, and document — a classic YAGNI trap. So the line is drawn at composition: **config declares
the wiring; code supplies the behavior, referenced from config by a registered id.**

### 1.3 What "config-ready" means for SP0 (the thing this ADR protects)

SP0 ships exactly **one** hand-coded `DemoMachine` and **no loader** — a loader for one machine is pure overhead. The
loader arrives at **SP1**, where the second machine (Atari 800) plus its first variant (800XL) make it pay. The risk
this ADR guards against is SP0 building device contracts that are *un-loadable* — devices whose construction is tangled
into a hand-coded composition method, with no stable id and no declarative param surface. The four design constraints in
§2.6 ("config-readiness") are the SP0-facing teeth of this ADR: they cost SP0 almost nothing now and save SP1 a
contract-reshaping pass.

### 1.4 What the shipped code already gives the loader (verified, not assumed)

- **`MachineBuilder` is already the right target.** A loader does not need a parallel construction path — it parses the
  manifest and calls the *same* `WithAddressSpace`/`WithRam`/`WithRom`/`WithPeripheral`/`WithCpu`/`Build()` methods. The
  product is byte-for-byte the same `Machine` the hand-coded path produces (Decision 5). (`MachineBuilder.cs:15–63`.)
- **The mapping rules are already enforced and already throw clear errors.** `AddressSpace.ValidateRange`
  (`AddressSpace.cs:200`) rejects non-page-aligned starts, non-page-multiple lengths, and out-of-space ranges;
  `EnsureRangeUnmapped` (`:213`) rejects overlaps with the exact offending page address. The builder rejects a duplicate
  space (`MachineBuilder.cs:17`), a missing CPU, and a missing Program space (`:57–60`). The loader's validation layer
  is *additive* — it catches manifest-level errors (unknown component id, bad params, dangling IRQ ref) **before** these
  builder/`AddressSpace` checks, so the user sees a manifest diagnostic, not a stack trace from deep in `Machine`'s
  constructor.
- **Per-space bus policy is already a small declarative record.** `AddressSpaceOptions` (`AddressSpaceOptions.cs`) is
  `{ OpenBusValue, Strict }` — two fields that map 1:1 to manifest keys (Decision 1).
- **IRQ/NMI wiring is already a per-device claim.** A device claims its line handle in `Realize` via
  `context.IrqLine.Source()` (e.g. `IntervalTimer.cs:44`). The wired-OR means the manifest's interrupt-wiring is mostly
  a *validation* concern (is the device the manifest wires to IRQ actually a device that claims IRQ?) plus, where a
  machine has more than the two built-in lines, a *line-selection* concern (§3.4, Open Question 5).
- **The CPU is chosen by a small factory.** `WithCpu(ctx => new Mos6502Cpu(ctx.Space(Program)))`. The manifest's `cpu`
  field selects which factory to call from a CPU registry (Decision 2), passing the resolved program space — exactly
  what the lambda does today.
- **The validation discipline already exists to copy.** `SpecDiagnostics.cs` defines `CPUGEN001..CPUGEN014` — numbered,
  titled, `Error`-severity descriptors with a `{0}` message slot — and the generator emits them when a CPU spec is
  malformed (missing registers, duplicate opcode, unknown micro-op, role violation). The manifest loader's diagnostics
  (§3.3) are the direct analogue: `MACHGEN0NN`, same shape, same "the data is wrong, here is exactly where" discipline.

---

## 2. Decisions

### Decision 1 — The dividing line: config = wiring, code = behavior

A machine manifest declares **composition** and nothing else. Concretely, **in** the manifest:

- **CPU choice** — a registered CPU id (`"mos6502"`, `"z80"`, `"m68000"`, `"i8086"`) selecting a CPU factory.
- **Address spaces** — one or more of `program` / `data` / `io` (mirrors `AddressSpaceKind`), each with `addressBits`
  (8..24, the ADR 0002 bound) and per-space bus policy (`openBusValue`, `strict` — the `AddressSpaceOptions` fields).
- **The memory map** — RAM and ROM regions: `base`, `size`, `writable`, optional `mirror`/partial-decode hints, and for
  ROM a file reference (`rom` + `sha256`, Decision 4). Each region is implicitly a **fastmem-RAM region** (ADR 0009
  Decision 1) because it is `MapMemory`-backed — the manifest does not need a separate "fastmem" flag for plain RAM/ROM;
  *RAM/ROM is fastmem by construction.* A device's fast-RAM region is declared by the device's component entry (below),
  not as a free-standing memory region.
- **Device instances** — by registered component id (`"mc6845"`, `"ay-3-8910"`, `"upd765"`, `"6551-acia"`) plus a
  `base` address, an optional explicit `size` (else the component's default window), and a `params` object validated
  against the component's declared param schema.
- **Interrupt wiring** — which device instances drive IRQ vs NMI (mostly validation today; a line-selection key for
  machines with >2 lines, §3.4 / Open Question 5).
- **Clock + display refresh** — the master clock (Hz) and, per display device, the refresh rate (which a display chip
  already schedules itself via `IScheduler`; the manifest value is the authoritative number the chip's `params` carry).
- **Per-device `TimingTier`** — `coarse` (default) or `fine` (ADR 0009 Decision 3), declared per device instance.
- **Simple, static bank/decode tables** — a *fixed* mapping table (e.g. "these 4 KiB ROM windows are pre-mapped at these
  bases"), expressible as ordinary memory regions. Anything whose mapping is *fixed at build time* is config.

Explicitly **NOT** in the manifest:

- **Device behavior** — register decode, side effects, timing algorithms. That is the `IPeripheral` implementation, full
  stop.
- **Behavioral / run-time bank-switching** — a C64 PLA, a cartridge mapper (MMC-class), Apple II soft-switches, PC
  EMS/UMB paging, the 68000 ROM-overlay-then-RAM boot trick. These are *algorithms that remap the bus in response to
  guest writes* and are exactly ADR 0009 Decision 2's `AddressSpace.Remap` path. They are implemented as a **code
  "mapper" component** (an `IPeripheral` that owns the bank-select registers and calls `Remap`), which the manifest
  references **by id** — e.g. `{ "component": "atari-cart-mapper", "base": "0xA000", "params": { "scheme": "atari800-16k", "rom": "cart.rom" } }`. The manifest says *which* mapper and *with what ROM*; the mapper's code does the switching.

**The hard rule: resist a Turing-complete DSL.** If expressing a machine feature in config would require conditionals,
loops, arithmetic on guest state, or "run this when the guest writes here," it is behavior → it belongs in a code
component referenced by id. The manifest stays a *data description of a static wiring diagram.*

**Rationale.**
- It puts the cheap, high-churn part (the numbers that differ between variants) in reviewable data and keeps the
  expensive, algorithmic part (behavior) in testable, debuggable, type-checked C# — the part the CPU side already proves
  is worth keeping as generated/handwritten code with ground-truth tests.
- It draws the line at precisely the seam ADR 0009 already cut: fastmem-RAM vs MMIO, static map vs `Remap`-driven map.
  The manifest does not invent a new axis; it is the declarative front-end to seams that already exist.
- It avoids the single largest failure mode of "machine description languages" (MAME's historically growing layout/INI
  surface, ad-hoc emulator config DSLs): scope creep into a half-language. A bright YAGNI line ("no behavior in config")
  keeps the format small and the validator tractable.

**Alternatives considered.**
- **(A) Pure code (status quo, no manifest).** *Rejected for the multi-variant future, kept for SP0.* Correct and
  simplest for one machine; becomes near-duplicate-class sprawl across variants (the SP1 trigger). The HYBRID keeps code
  for behavior, so this is not fully rejected — it is *narrowed* to behavior.
- **(B) Pure config (a machine DSL that can express behavior too).** *Rejected.* This is the Turing-complete-DSL trap: a
  second language to build and maintain, slower than compiled C# for hot device paths, and impossible to unit-test with
  the TomHarte-style oracles the CPU side relies on. The owner's HYBRID call explicitly avoids it.
- **(C) Config + scripting hook (embed Lua/JS for behavior).** *Rejected for now.* It re-introduces a second language and
  a perf cliff over the hot device paths ADR 0009 Decision 1 worked to keep on fastmem. If a *user-extensibility* story
  ever demands plug-in behavior without recompiling, revisit — but the in-tree machines (Atari, PC) are all C#
  components, so it is unbudgeted YAGNI.

**Consequences.**
- *Good:* variants are data diffs (Decision 6); the format is small and validatable; behavior stays fast and testable.
- *Good:* the manifest is consistent with ADR 0009 by construction — it can only express what the seams allow (a static
  region is fastmem; a remap is a code mapper).
- *Bad / accepted:* a machine's *full* definition is split across two artifacts (a manifest + the referenced
  components), so "what is this machine" is read in two places. Mitigated by the registry being the index: the manifest
  names ids, the registry resolves them, and a `--describe` dump (Open Question 4) can print the fully-resolved machine.
- *Bad / accepted:* the boundary "is this wiring or behavior?" has genuine edge cases (a *fixed* bank table is config; a
  *conditional* one is code). The rule (no conditionals/loops/guest-state-arithmetic in config) is the tie-breaker, and
  edge cases default to code (the safe side — code can always express what config can).

### Decision 2 — The component registry: id → `IPeripheral` factory + param schema, with loader-time validation

A **component registry** maps a manifest component id to (a) a factory that builds the `IPeripheral` (and any capability
interfaces it composes — `IDisplayDevice`, `IFastMemoryProvider`, `ITimingSensitive`, a mapper's `Remap` caller) and (b)
a **param schema** the loader validates the manifest's `params` object against before constructing anything. A parallel
**CPU registry** maps a CPU id to its `ICpuCore` factory.

A component **self-registers** by carrying a small descriptor — id, display name, default window size, the param schema,
and the factory delegate. The mechanism mirrors how a CPU spec is a `[CpuSpecification]`-attributed type the generator
discovers: a component is an attributed type the registry discovers (reflection over the loaded peripheral assemblies at
startup, or an explicit registration list — Open Question 2 picks one).

**Sketch of the registration surface** (shapes, not implementations — all additive to `CpuEmulator.Core`):

```csharp
namespace CpuEmulator.Core.Machines;   // new namespace; the config layer

/// <summary>One parameter a component accepts from a manifest's "params" object. The schema is the
/// loader's contract: it validates type/range/required-ness BEFORE the factory runs, so a bad param
/// is a MACHGEN diagnostic (pointing at the manifest key) not a constructor throw.</summary>
public sealed record ComponentParam(
    string Name,
    ComponentParamKind Kind,         // Int | Hex | Bool | String | Enum | RomRef
    bool Required,
    object? Default = null,
    long? Min = null, long? Max = null,
    IReadOnlyList<string>? EnumValues = null);

public enum ComponentParamKind { Int, Hex, Bool, String, Enum, RomRef }

/// <summary>What the factory receives: the validated params (typed accessors), the machine context
/// (scheduler/spaces/IRQ — same as IPeripheral.Realize sees), and resolved ROM/asset blobs (Decision 4).</summary>
public interface IComponentBuildContext
{
    long   Int(string name);
    uint   Hex(string name);
    bool   Bool(string name);
    string Str(string name);
    byte[] Rom(string name);         // a RomRef param, already loaded + hash-verified
    IMachineContext Machine { get; }
}

/// <summary>A registered peripheral component. The registry resolves a manifest id to this.</summary>
public sealed record ComponentDescriptor(
    string Id,                       // manifest id, e.g. "mc6845", "ay-3-8910", "upd765"
    string DisplayName,
    uint DefaultWindowSize,          // page-multiple; the mapping length if the manifest omits "size"
    IReadOnlyList<ComponentParam> Params,
    Func<IComponentBuildContext, IPeripheral> Factory);

public sealed class ComponentRegistry
{
    public void Register(ComponentDescriptor descriptor);          // self-registration entry point
    public bool TryResolve(string id, out ComponentDescriptor d);  // loader lookup
    public IReadOnlyCollection<ComponentDescriptor> All { get; }   // for "unknown id: did you mean…" + --list
}

// CPU side, symmetric and smaller:
public sealed record CpuDescriptor(string Id, Func<IMachineContext, ICpuCore> Factory);
public sealed class CpuRegistry { /* Register / TryResolve / All, same shape */ }
```

**Loader validation = the CPUGEN analogue (the `MACHGEN0NN` set).** The loader validates the parsed manifest against the
registries and the `AddressSpace` rules, emitting numbered diagnostics exactly like `SpecDiagnostics.cs`, before it
calls a single `MachineBuilder` method. The diagnostic catalog (full text in §3.3):

| Diagnostic | Condition |
|---|---|
| `MACHGEN001` unknown CPU id | `cpu` not in `CpuRegistry` (with "did you mean" over `All`) |
| `MACHGEN002` unknown component id | a device's `component` not in `ComponentRegistry` |
| `MACHGEN003` missing required param | a `Required` `ComponentParam` absent from a device's `params` |
| `MACHGEN004` bad param type/range | a param fails its `ComponentParamKind` / `Min`/`Max` / `EnumValues` check |
| `MACHGEN005` unknown param key | a `params` key not in the component's schema (typo guard) |
| `MACHGEN006` overlapping regions | two regions/devices claim the same page (pre-empts `EnsureRangeUnmapped`) |
| `MACHGEN007` mis-aligned / non-page-multiple region | violates `ValidateRange` (pre-empts it with a manifest pointer) |
| `MACHGEN008` region out of address space | `base+size` exceeds the space's `addressBits` window |
| `MACHGEN009` dangling interrupt wiring | `interrupts.irq` names a device that claims no IRQ line, or an unknown device |
| `MACHGEN010` missing ROM / hash mismatch | a `RomRef` file is absent or its `sha256` does not match (Decision 4) |
| `MACHGEN011` no program space / no CPU | structural (pre-empts the `MachineBuilder` throws) |
| `MACHGEN012` unmapped-but-referenced / duplicate device name | a referenced instance name is undefined, or two instances share a name |

The point of pre-empting the `AddressSpace`/builder throws (006–008, 011) is a *manifest-coordinate* error message ("at
`devices[2].base` (0xD080): not page-aligned") instead of a runtime exception from inside `Machine`'s constructor — the
same reason CPUGEN validates the spec at generate-time rather than letting the CPU crash at run-time.

**Rationale.**
- The registry is the indirection that makes "by id" possible at all, and it is the *only* new runtime concept the
  HYBRID needs. Everything else (the mapping, the wiring) reuses `MachineBuilder`.
- Validating params against a declared schema *before* construction is what turns a class of runtime crashes into
  actionable data errors — the CPU side's hardest-won lesson (CPUGEN catches a malformed spec at build time; a malformed
  manifest should be caught the same way). A param schema is also self-documenting: `--list` / `--describe` can print
  every component's accepted params from the same descriptors the validator uses.
- Component self-registration via an attribute mirrors `[CpuSpecification]`, keeping one discovery story across CPUs and
  devices.

**Alternatives considered.**
- **(A) No registry — the manifest names a fully-qualified .NET type, instantiated by reflection.** *Rejected.* It
  couples the manifest to assembly/namespace layout (a refactor breaks every machine file), offers no param schema (so
  no pre-construction validation), and exposes arbitrary-type instantiation (a mild security/footgun surface). A stable,
  short id decoupled from type layout is strictly better.
- **(B) Registry but no param schema — pass the raw JSON params object to the factory and let it validate.** *Rejected
  as the default.* It scatters validation across every component (each re-implements "is this hex in range"), produces
  inconsistent error messages, and loses the `--list`-able self-documentation. A declared schema centralizes it. (A
  component with genuinely irregular params can still take a free-form `String`/JSON param and parse it itself — the
  escape hatch exists without making it the norm.)
- **(C) Code-gen the registry from the component set (a build step).** *Deferred (an M6-style optimization, not a
  Decision).* Reflection-at-startup is fine for tens of components; if startup cost or trimming/AOT ever matters, a
  source-generated registration list is the drop-in replacement. Not needed now.

**Consequences.**
- *Good:* manifests reference stable ids, decoupled from type layout; params are validated and self-documenting;
  diagnostics are CPUGEN-consistent; one discovery story for CPUs and devices.
- *Bad / accepted:* a new registry type + a descriptor per component is real surface to maintain, and a component author
  must keep the descriptor's param schema in sync with the constructor. Mitigated by the descriptor being *the* source
  of truth the factory reads params from (`IComponentBuildContext` accessors), so a param the factory reads but did not
  declare fails fast in a registry self-test.
- *Bad / accepted:* reflection discovery has a startup cost and an AOT/trimming caveat (alternative C is the exit).

### Decision 3 — Format = JSON, consistent with the CPU spec datasets

The manifest is **JSON**, matching the CPU spec datasets (`tools/CpuEmulator.SpecImporter/data/*.json`). Numbers that are
addresses are written as hex *strings* (`"0xD000"`) — JSON has no hex literal, and the CPU opcode dataset already uses
`"opcode": "0x00"` strings, so this is the project's established convention. A small worked manifest (the Breadboard-class
reference, Decision 5) — full schema in §3.1:

```jsonc
{
  "schema": "cpuemu.machine/1",
  "name": "breadboard6502",
  "cpu": "mos6502",
  "clockHz": 1000000,
  "spaces": [
    { "kind": "program", "addressBits": 16, "openBusValue": "0xFF", "strict": false }
  ],
  "memory": [
    { "kind": "program", "base": "0x0000", "size": "0xD000", "writable": true },         // RAM 52 KiB
    { "kind": "program", "base": "0xE000", "size": "0x2000", "writable": false,          // ROM 8 KiB
      "rom": "roms/demo.bin", "sha256": "<hex>" }
  ],
  "devices": [
    { "name": "uart",  "component": "6551-acia",   "kind": "program", "base": "0xD000",  // 1 page
      "timingTier": "coarse", "params": { "baud": 19200 } },
    { "name": "timer", "component": "interval-timer","kind": "program", "base": "0xD100", // 1 page
      "timingTier": "coarse", "params": {} }
  ],
  "interrupts": { "irq": ["uart", "timer"], "nmi": [] }
}
```

**Rationale.** JSON is what the codebase already parses for specs; reusing it means one parsing/style story, no new
dependency, and the hex-string convention is already in the data. It is diff-friendly (Decision 6 variants), human-
authorable, and trivially machine-generated (a `--describe` could emit the resolved manifest).

**Alternatives considered.**
- **(A) YAML.** *Rejected.* Friendlier for hand-authoring (comments, less punctuation), but it is a new dependency and a
  second data-format style in a repo that is uniformly JSON for specs. The JSON-with-`//`-comments (JSONC) the example
  uses for illustration is a doc convenience; the on-disk format is strict JSON (Open Question 6 covers whether to allow
  comments on disk).
- **(B) TOML / INI.** *Rejected.* Pleasant for flat config, awkward for the nested arrays-of-objects a memory map and
  device list are; and again a new format.
- **(C) A C# DSL / builder script compiled at load (Roslyn-scripted).** *Rejected.* That is just code-as-config with a
  compile step — it abandons the data-diff and validate-before-construct wins and reintroduces the scripting-language
  surface Decision 1(C) rejected.

**Consequences.** *Good:* one format across the repo, no new dependency, diff-friendly, generatable. *Bad / accepted:*
JSON has no comments (strict) and no hex literals (hence the string convention) — minor authoring friction, the cost of
format uniformity.

### Decision 4 — ROM/asset references: path + content hash, loaded into declared regions

A ROM or asset is referenced by a **relative path** plus a **`sha256`** content hash. The loader resolves the path
(relative to the manifest, with a configured asset root), reads the bytes, **verifies the hash** (mismatch →
`MACHGEN010`), and loads them into the declared region: a `memory` entry with a `rom` ref becomes a `WithRom(kind, base,
bytes)` call (its `size` must equal the file length — a mismatch is `MACHGEN010`); a device with a `RomRef` param
receives the verified bytes via `IComponentBuildContext.Rom(name)` (a cartridge mapper's ROM image, a character ROM).

**Rationale.**
- The hash makes a machine definition **reproducible and tamper-evident**: a manifest pins *exactly* which ROM image it
  was authored against, so "it works on my machine" ROM-version drift becomes a loud `MACHGEN010` instead of silent
  wrong behavior. This matters most for variants (Decision 6), which differ *primarily* by ROM.
- Keeping ROMs out-of-band (referenced, not inlined) keeps the manifest small, diffable, and free of large base64 blobs,
  and sidesteps the licensing/redistribution question for copyrighted ROMs (the manifest can ship; the ROM is the user's
  to supply, and the hash tells them if they supplied the right one).

**Alternatives considered.**
- **(A) Inline base64 ROM bytes in the manifest.** *Rejected.* Bloats the file, kills diffability, and bakes
  potentially-copyrighted bytes into the repo.
- **(B) Path only, no hash.** *Rejected.* Loses reproducibility — the silent-wrong-ROM failure is exactly the bug a hash
  prevents, and it is a nasty one (the machine boots but misbehaves subtly).
- **(C) A separate ROM-manifest / content-addressed store.** *Deferred / YAGNI.* Over-engineered for the handful of ROMs
  a few machines need; the path+hash inline reference is enough until a shared ROM library exists.

**Consequences.** *Good:* reproducible, tamper-evident, license-friendly, small manifests. *Bad / accepted:* the user
must supply ROMs out-of-band and a hash mismatch blocks load (the intended behavior, but it is a setup step). Open
Question 3 covers the hash algorithm/optionality knob.

### Decision 5 — Relationship to `Machine`/`Breadboard6502`: the loader is additive; it produces the same `Machine`

The loader is a **pure front-end to `MachineBuilder`.** It parses + validates the manifest, resolves ids through the
registries, loads ROMs, and then calls the *existing* builder methods — producing a `Machine` indistinguishable from one
the hand-coded path builds. There is **no second construction path, no `Machine` change.** `Breadboard6502` is the
**reference fixture**: the loader's correctness test is "a manifest that describes the breadboard produces a `Machine`
behaviorally identical to `new Breadboard6502().Machine`" (same memory map, same devices at same bases, same boot
behavior under the demo ROM). This mirrors the CPU side's JIT-vs-interpreter parity discipline: the new path must
reproduce the proven path exactly.

```text
manifest.json ─▶ parse ─▶ validate (MACHGEN0NN) ─▶ resolve (CpuRegistry/ComponentRegistry, load ROMs)
              ─▶ MachineBuilder.WithAddressSpace/.WithRam/.WithRom/.WithPeripheral/.WithCpu ─▶ Build() ─▶ Machine
```

**Rationale.** Reusing `MachineBuilder` means the loader inherits every invariant the builder and `Machine`'s two-phase
construction already guarantee (phase-ordering, the `Realize` discipline ADR 0009 depends on, the mapping checks) for
free, and there is one `Machine` semantics to reason about. A parallel construction path would be a second place for the
phase-ordering and ADR-0009 seams to drift.

**Alternatives considered.**
- **(A) Loader builds `Machine` directly (its own construction path).** *Rejected.* Duplicates the two-phase discipline
  and the mapping validation; invites drift from the hand-coded path's invariants.
- **(B) Loader emits C# source that is then compiled (a code-gen front-end).** *Rejected.* Adds a compile step and a
  generated-artifact to manage for zero benefit over calling the builder directly at load time; only attractive if
  manifests were *also* the source-of-truth for the hand-coded machines, which they are not (the HYBRID keeps both).

**Consequences.** *Good:* one `Machine` semantics; loader inherits all builder invariants; a clean parity test against
the reference. *Bad / accepted:* the loader can only express what `MachineBuilder` exposes — if a future machine needs a
composition primitive the builder lacks (e.g. a manifest-declared *static* multi-mapping), the builder gains a method
first, then the loader exposes it (the builder stays the source of truth, which is correct).

### Decision 6 — Variants are small manifest diffs

A machine *variant* (same family, different RAM/ROM/devices) is a **separate manifest that differs by a few keys** —
the payoff that justifies the loader at SP1. Sketched as the Atari 800 family (illustrative addresses; SP1 nails the real
map against the ANTIC/GTIA/POKEY register sheets):

- **Atari 800** (base): 48 KiB RAM, OS ROM rev A.
- **800XL**: 64 KiB RAM, OS ROM rev B, a different keyboard/PIA wiring.
- **130XE**: 128 KiB RAM via a banked-window mapper component.

```jsonc
// atari-800.json   (excerpt — the family base)
{ "name": "atari-800", "cpu": "mos6502", "clockHz": 1789790,
  "spaces": [{ "kind": "program", "addressBits": 16 }],
  "memory": [
    { "kind": "program", "base": "0x0000", "size": "0xC000", "writable": true },          // 48 KiB RAM
    { "kind": "program", "base": "0xD800", "size": "0x2800", "writable": false,
      "rom": "roms/atariosa.rom", "sha256": "<A>" }                                        // OS ROM rev A
  ],
  "devices": [
    { "name": "antic", "component": "antic", "kind": "program", "base": "0xD400",
      "timingTier": "fine", "params": { "refreshHz": 59.92 } },
    { "name": "gtia",  "component": "gtia",  "kind": "program", "base": "0xD000", "timingTier": "fine" },
    { "name": "pokey", "component": "pokey", "kind": "program", "base": "0xD200", "timingTier": "coarse" }
  ],
  "interrupts": { "irq": ["pokey"], "nmi": ["antic"] }   // ANTIC DLI/VBI → NMI; POKEY serial/timer → IRQ
}
```

```diff
// atari-800xl.json — DIFF vs atari-800.json (only the changed keys)
   "name": "atari-800",                                  →  "name": "atari-800xl",
-  { "base": "0x0000", "size": "0xC000", ... },          // 48 KiB
+  { "base": "0x0000", "size": "0x10000", ... },         // 64 KiB RAM
-  { "rom": "roms/atariosa.rom", "sha256": "<A>" }
+  { "rom": "roms/atarixl.rom",  "sha256": "<B>" }        // OS ROM rev B
   // + a PIA/keyboard device-wiring tweak
```

```diff
// atari-130xe.json — DIFF vs atari-800xl.json
+  { "name": "banker", "component": "xe-bank-mapper", "kind": "program", "base": "0xD301",
+    "params": { "extraRamKiB": 64, "window": "0x4000" } }   // 128 KiB via a banked $4000 window (code mapper, ADR 0009 D2)
```

The PC family (PC vs PC/XT) is the same story on the 8086 side (different BIOS ROM hash, RAM size, and an XT adds a fixed-
disk controller device entry). **Note the HYBRID line in action:** the 130XE's extra 64 KiB is *behavioral* banking, so
it is a **code mapper component** (`xe-bank-mapper`, an `IPeripheral` that calls `AddressSpace.Remap` — ADR 0009
Decision 2) referenced by id with params; the manifest does not describe the banking algorithm, only that the machine
*has* that mapper with that much RAM and that window.

**Rationale.** This is the entire economic case for the loader: a variant is a reviewable data diff, not a new compiled
class. The diff *is* the documentation of how the variant differs. **Consequences.** *Good:* variants are cheap and
self-documenting. *Bad / accepted:* near-identical manifests can drift (a fix to the base not propagated to a variant);
a future manifest-`extends`/include mechanism (Open Question 1) could DRY them up, deferred until the duplication
actually bites (likely the third variant in a family).

### Decision 7 — Rollout/timing: SP0 hand-coded (loader is YAGNI for one), loader at SP1

- **SP0** ships **one** hand-coded `DemoMachine` and **no loader.** A loader for a single machine is overhead with no
  payoff. SP0's obligation to this ADR is **config-readiness** (§2.6 below), not the loader.
- **SP1** (Atari 800 + the first variant, 800XL) is where the loader **pays**: two-plus machines and a variant make the
  data-diff win real. The loader, the registries, and the `MACHGEN` validator are SP1 deliverables. SP1 authors the
  `cpuemu.machine/1` schema against the *first real* register maps, not a guessed one (the same "reverse-engineer the
  format from the first real instance" discipline ADR 0009 Decision 4 applied to device specs).
- **SP3** (PC clone) exercises the format's generality on a completely different device ecosystem and the PC/PC-XT
  variant pair.

**§2.6 — Config-readiness obligations for SP0 (the SP0-facing teeth of this ADR).** SP0 must, at near-zero extra cost,
land its devices so a manifest can later instantiate them:

1. **Each device is a self-contained `IPeripheral` + capability interfaces** (the SP0 contracts already do this:
   `DemoFramebuffer` = `IPeripheral` + `IFastMemoryProvider` + `IDisplayDevice`). No device construction is entangled in
   the `DemoMachine` composition method beyond `new`-ing it and passing params.
2. **Each device's construction parameters are explicit constructor args / a small options record** — not magic numbers
   buried in the composition method. (A manifest `params` object maps onto exactly these.) E.g. `DemoFramebuffer(width,
   height, paletteRomRef)` not a hard-coded 256×192.
3. **A stable conceptual id per device type** is reserved (even if the registry does not exist yet) — `"demo-framebuffer"`,
   `"demo-keyboard"`, `"demo-disk"` — so the SP1 registry can register them without renaming.
4. **The `DemoMachine` composition is structured as "resolve params → build device → `WithPeripheral`"** so it reads as
   what the loader will do procedurally — i.e. `DemoMachine` is the hand-coded shadow of a manifest, making the SP1
   "lift it into a manifest" step mechanical.

These cost SP0 essentially nothing (they are good factoring regardless) and save SP1 from a contract-reshaping pass.

**Rationale.** Building the loader at SP0 would be speculative generality (YAGNI — one machine needs no loader); *not*
making SP0 config-ready would force an SP1 rework of the device contracts. The split — defer the machinery, enforce the
boundaries — is the cheap insurance. **Consequences.** *Good:* no premature loader; SP1 lifts SP0's machine into a
manifest mechanically. *Bad / accepted:* SP0 carries four small factoring constraints whose payoff is deferred to SP1
(acceptable — they are good practice independent of the loader).

---

## 3. Concrete schema + surfaces

### 3.1 The `cpuemu.machine/1` top-level schema

```jsonc
{
  "schema": "cpuemu.machine/1",         // format version tag (Open Question 6: how versions evolve)
  "name":   "<string>",                 // machine name → Machine.Create(name)
  "cpu":    "<cpu-id>",                  // CpuRegistry id → WithCpu(factory)
  "clockHz": <int>,                      // master clock; the scheduler's cycle = 1/clockHz s

  "spaces": [                            // 1..3, one per AddressSpaceKind used
    { "kind": "program|data|io",
      "addressBits": <8..24>,            // ADR 0002 bound
      "openBusValue": "<hex byte>",      // AddressSpaceOptions.OpenBusValue (default "0xFF")
      "strict": <bool> }                 // AddressSpaceOptions.Strict (default false)
  ],

  "memory": [                            // RAM/ROM regions → WithRam / WithRom (fastmem by construction)
    { "kind": "<space>",
      "base": "<hex, page-aligned>",
      "size": "<hex, page-multiple>",
      "writable": <bool>,                // false ⇒ ROM
      "rom":    "<relative path>",       // ROM only: the image file (Decision 4)
      "sha256": "<hex>",                 // ROM only: content hash, verified at load
      "mirror": { "stride": "<hex>", "count": <int> } }  // OPTIONAL static mirroring (Open Question 7)
  ],

  "devices": [                           // device instances → WithPeripheral, after registry resolve
    { "name":      "<unique instance name>",   // referenced by "interrupts"
      "component": "<component-id>",            // ComponentRegistry id
      "kind":      "<space>",
      "base":      "<hex, page-aligned>",
      "size":      "<hex, page-multiple>",      // OPTIONAL; default = ComponentDescriptor.DefaultWindowSize
      "timingTier": "coarse|fine",              // ADR 0009 Decision 3; default "coarse"
      "params":    { /* validated against ComponentDescriptor.Params */ } }
  ],

  "interrupts": {                        // wiring + validation (MACHGEN009)
    "irq": [ "<device-name>", ... ],     // instances that drive IRQ (wired-OR — validation-only today)
    "nmi": [ "<device-name>", ... ]      // instances that drive NMI
  }
}
```

Field-to-seam mapping (the manifest is a thin skin over existing calls):

| Manifest | Maps to |
|---|---|
| `cpu` | `CpuRegistry.TryResolve(cpu)` → `WithCpu(descriptor.Factory)` |
| `spaces[]` | `WithAddressSpace(kind, addressBits, new AddressSpaceOptions { OpenBusValue, Strict })` |
| `memory[]` writable | `WithRam(kind, base, size)` |
| `memory[]` rom | load+verify bytes (Decision 4) → `WithRom(kind, base, bytes)` |
| `devices[]` | `ComponentRegistry.TryResolve(component)` → validate `params` → `Factory(buildCtx)` → `WithPeripheral(kind, base, size, device)` |
| `devices[].timingTier` | the device's `ITimingSensitive.TimingTier` (ADR 0009 §3.3); the component honors it |
| `interrupts` | validated against which devices claim `IrqLine.Source()` / `NmiLine.Source()` |

### 3.2 The fastmem / MMIO / remap consistency with ADR 0009 (no new runtime seam)

The manifest does **not** introduce any new fastmem or invalidation mechanism — it is purely declarative over ADR 0009's
seams:

- A `memory[]` region is `MapMemory`-backed ⇒ `Fastmem` classifies it non-null ⇒ it is a **fastmem-RAM region** (ADR
  0009 D1) automatically. No flag needed.
- A `devices[]` entry whose component implements `IFastMemoryProvider` (a framebuffer's VRAM) contributes **both** a
  fastmem region (its backing array, via `MapMemory`) and an MMIO register window (via `MapPeripheral`) — exactly the
  two-region split of ADR 0009 §3.1. The manifest declares the device once (`base` = its register window); the
  component's `FastRegions` declare the fast VRAM base/size. The loader honors both when wiring (the manifest does not
  separately declare the fast region — it comes from the component, so a manifest cannot get the split wrong).
- **Remapping is never in the manifest.** A bank-switching machine references a **code mapper component** (Decision 1)
  that owns the `Remap` call (ADR 0009 D2). The manifest's only banking-adjacent content is a *static* pre-mapped table
  (ordinary `memory[]` regions) or the mapper's `params` (which ROM, which scheme, how much RAM).
- `timingTier` is the literal ADR 0009 D3 `TimingTier` value, declared per instance.

This is the load-bearing consistency guarantee: **the manifest can only express ADR-0009-legal compositions, because its
vocabulary is exactly the builder's `MapMemory`/`MapPeripheral` + the component capability interfaces.**

### 3.3 The `MACHGEN0NN` diagnostic catalog (the CPUGEN analogue)

Modeled on `src/CpuEmulator.Generators/SpecDiagnostics.cs` — numbered, titled, `Error` severity, a `{0}` message slot
carrying the manifest coordinate. Full list in Decision 2's table; the *shape* (matching `SpecDiagnostics.Make`):

```csharp
// MACHGEN006, e.g. — overlapping regions, with the manifest coordinate in the message:
//   "MACHGEN006: Overlapping regions — devices[2] 'gtia' (0xD000–0xD0FF) overlaps memory[1] ROM (0xD000–...)."
```

The loader runs **all** validations and reports **all** diagnostics (not fail-on-first), so a user fixes a manifest in
one pass — the CPUGEN generator's batch-diagnostic behavior.

### 3.4 Interrupt wiring: validation today, line-selection later

With the two built-in wired-OR lines (`IrqLine`/`NmiLine`) and per-device `Source()` claiming, `interrupts` is mostly a
**validation + documentation** surface: `MACHGEN009` confirms every name under `irq`/`nmi` is a real instance that
actually claims the corresponding line in `Realize`. It documents the machine's interrupt topology in one readable place
(today that is implicit in each device's `Realize`). For a machine with a programmable interrupt controller (the PC's
8259) or more than two CPU lines, the wiring becomes *selection* (which device → which of N lines), which the current
two-line model does not express — flagged as Open Question 5, to be resolved against the 8259 at SP3.

---

## 4. Consequences (cross-cutting)

**Good.**
- A second/third machine and their variants become **data**, not near-duplicate C# classes (Decision 6) — the arc's
  composability premise made concrete.
- The format is **small and validatable**: a bright "no behavior in config" line (Decision 1) keeps it from becoming a
  half-language, and the `MACHGEN` validator (Decision 2) catches manifest errors as actionable data diagnostics, the
  CPUGEN discipline applied to machines.
- It is **consistent with ADR 0009 by construction** (§3.2): the manifest's vocabulary is exactly the builder's
  fastmem/MMIO seams + the component capabilities, so a manifest cannot express an ADR-0009-illegal composition.
- The loader is **additive** (Decision 5): one `Machine` semantics, all builder invariants inherited, a clean parity
  test against `Breadboard6502`.
- ROM references are **reproducible and tamper-evident** (Decision 4).

**Bad / accepted costs.**
- A machine's definition is **split across a manifest + its code components** (Decision 1) — read in two places,
  mitigated by the registry index + a `--describe` dump.
- A **new registry + a descriptor per component** is maintenance surface (Decision 2), and reflection discovery has an
  AOT/trimming caveat (alternative C is the exit).
- **Variant manifests can drift** absent a DRY mechanism (Decision 6 / Open Question 1).
- **Nothing is built until SP1**; SP0 carries four small config-readiness constraints (Decision 7 §2.6) whose payoff is
  deferred.

**Reversibility.** High. The loader is a pure front-end to `MachineBuilder`; if the manifest format proves wrong, the
hand-coded path still works unchanged and a v2 schema (`cpuemu.machine/2`) can supersede v1 with the version tag already
in place (Open Question 6). No `Machine`/`AddressSpace`/`IPeripheral` change is required by this ADR — the only new
runtime types are the registries and the loader, both additive and behind no existing call.

---

## 5. Open questions

1. **Manifest DRY for variants (Decision 6).** Do variants get an `extends`/`include` mechanism (a base manifest +
   override keys) to avoid copy-drift, or stay independent files? Leaning *independent files until the third variant in a
   family* (the duplication is the trigger, not the second file). Resolve when the Atari XL/XE pair lands at SP1.
2. **Component discovery mechanism (Decision 2).** Reflection over `[Component]`-attributed types in the loaded
   peripheral assemblies (mirrors `[CpuSpecification]`) vs. an explicit registration list vs. a source-generated registry
   (alternative C). Leaning reflection-at-startup for SP1 (fewest moving parts), with source-gen as the AOT/trim exit if
   needed. Confirm at SP1 plan time against the AOT story (if any).
3. **ROM hash algorithm + optionality (Decision 4).** `sha256` is the proposal; is the hash *mandatory* (strictest,
   most reproducible) or optional-with-warning (friendlier for quick experiments, a `MACHGEN` *Warning* not *Error*)?
   And is one algorithm enough? Leaning mandatory-`sha256`-with-an-explicit-`"sha256": "skip"` escape for throwaway
   manifests. Owner's call.
4. **A `--describe` / `--list` / `--validate` CLI surface.** Should the loader expose (a) `--validate manifest.json`
   (run `MACHGEN` only, no construction — a lint), (b) `--list` (dump the registry: every component id + its param
   schema), (c) `--describe manifest.json` (the fully-resolved machine: regions, devices, addresses)? These fall out of
   the registry/validator for near-free and are high-value for authoring; confirm scope at SP1.
5. **Interrupt wiring beyond two lines (Decision 1 / §3.4).** The current `IrqLine`/`NmiLine` two-line model + wired-OR
   makes `interrupts` validation-only. The PC's 8259 PIC needs *selection* (device → one of N inputs) and is itself a
   *device* the CPU's single IRQ line hangs off. Does the manifest need a richer interrupt-topology surface, or does the
   8259-as-a-device model (the guest programs the PIC; the CPU sees one line) make the manifest's job stay validation +
   "which device is the PIC"? Expected the latter; confirm against the 8259 at SP3.
6. **Schema versioning + comments (Decision 3).** The `"schema": "cpuemu.machine/1"` tag is in place; the policy for
   evolving it (additive keys in v1 vs. a v2 bump; whether the loader accepts unknown keys as forward-compat warnings or
   `MACHGEN` errors) is unset. Also: is on-disk JSONC (comments) allowed, or strict JSON only? Leaning additive-in-v1,
   unknown-key = `MACHGEN005` error (typo guard wins over forward-compat for hand-authored files), strict JSON on disk.
7. **Static mirroring / partial-decode in the memory map (§3.1 `mirror`).** A real bus mirrors a small region across a
   larger window (the breadboard's timer "mirrors every 4 bytes" — `IntervalTimer.cs`; the 6502 zero-page/stack image).
   Sub-page partial decode is *already the peripheral's job* (`AddressSpace.cs:5` comment) and a device-internal
   `offset & mask` handles it (as `IntervalTimer`/`SimpleUart` do) — so device-level mirroring needs **no** manifest
   support. The open question is only whether *RAM/ROM*-level mirroring (a ROM image appearing at multiple bases) is
   common enough to warrant the `mirror` key, or whether repeating the `memory[]` entry per base is fine. Leaning
   "repeat the entry until a machine has many mirrors"; confirm against the first machine that mirrors ROM (some
   cartridge layouts).
8. **Where the loader lives + the manifest schema's home (Decision 5).** A new `CpuEmulator.Machines` project is SP0's
   home for `DemoMachine` (SP0 §3); the loader + registries presumably live there or in a sibling `CpuEmulator.Config`.
   And does a JSON-Schema file ship alongside `cpuemu.machine/1` for editor validation? Deferred to SP1's project
   layout.

---

*End of ADR 0010. The HYBRID: a JSON manifest declares machine **wiring** (CPU, memory map, device instances by
registered id + params, interrupt wiring, clock/refresh, per-device `TimingTier`); device **behavior** stays code
(`IPeripheral` components resolved through a `ComponentRegistry`, including run-time bank-switching as code "mapper"
components referenced by id). The format is JSON (Decision 3), consistent with the CPU spec datasets; ROMs are
referenced by path + `sha256` (Decision 4); the loader is a pure front-end to `MachineBuilder` producing the same
`Machine` the hand-coded path does (Decision 5), with `Breadboard6502` as the parity reference. Validation mirrors
CPUGEN as a `MACHGEN0NN` diagnostic set (Decision 2). The manifest is consistent with ADR 0009 by construction: its
vocabulary is exactly the builder's fastmem/MMIO seams + the device capability interfaces (§3.2). Rollout is YAGNI-
disciplined: SP0 keeps one hand-coded machine and only owes config-readiness (Decision 7); the loader arrives at SP1
where a second machine + first variant make it pay. Designer: the only UX-adjacent surface is the optional authoring CLI
(`--validate`/`--list`/`--describe`, Open Question 4) — no end-user UI. Planner can expand §2/§3 into SP1 loader +
registry + validator tasks once the owner signs off.*
