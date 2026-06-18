# M6 PR-0 — the shared wide-register emit helper (the un-blocker)

> **STATUS: PLAN — preparatory doc. The implementation is a `src/`-touching JIT change, so it lands on a
> branch + PR (per the workflow), NOT straight to main.**
> **For agentic workers:** REQUIRED SUB-SKILL once scheduled — use `superpowers:subagent-driven-development`
> or `superpowers:executing-plans` to implement task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
> **Read first (binding):** ADR 0011 §8 PR-0 row (the scope), §3 Decision 2 ("what genuinely generalizes" —
> the register-file is data-driven, the pair-views are the one gap), and ADR §7 OQ6 (the Z80 pair-view
> helper). This plan EXTENDS the existing `BlockCompiler.*` JIT assembly; it does NOT invent a new emit model.

---

## Objective (the ADR §8 PR-0 row)

Add the **reusable 16-bit / wide-register read-write emit primitive** that the structured-CPU emit arms
need, on the JIT side ONLY (`src/CpuEmulator.Jit/BlockCompiler.*`). Today the per-compile register-file map
`_regFields` (`BlockCompiler.cs:96-107`) covers only **field-backed** registers and deliberately **SKIPS the
field-less pair-views** — the Z80's `AF/BC/DE/HL/IX/IY` (+ shadow set) are composed `ushort` *properties*
over two `byte` half-fields, not fields, so `target.CpuType.GetField(name)` returns null and they are absent
from `_regFields`. PR-1 (Z80 `LD`) cannot emit `LD BC,nn` / `LD HL,(nn)` / `LD (HL),r` until an emit arm can
read and write these pairs.

**This PR ships the helper, with zero op-emit and zero generator change.** It is pure register-file plumbing:
a pair of emit methods that, given a 16-bit register *name*, emit the IL to (a) push the pair's current value
as a `ushort`/`int` onto the IL stack, and (b) pop a value off the stack into the pair — composing/decomposing
the two `byte` half-fields for the property-backed pairs (`AF/BC/DE/HL/IX/IY/…_`), and using a direct
`Ldfld/Stfld` for the genuinely-field-backed `ushort` registers (`SP/PC/WZ`). Because no descriptor is yet
emittable through it, **it changes no measured throughput** — its gate is a JIT unit test that round-trips
every pair, not a benchmark delta (§5 honesty gate: no measured-data claim, because it emits no op yet).

**Why first (ADR §8):** PR-1 is blocked on it. Building it standalone keeps PR-1 a *pure emit* PR and gives
Decision 2 its first "what generalizes" data point — the 68000's 32-bit `D/A` registers and the 8086's
`AX/AL/AH` overlap reuse the **same compose/decompose shape**, so this helper is shared infrastructure all
three structured CPUs draw on (confirmed: the 8086's `AX/BX/CX/DX` are pair-view properties skipped by the
same `_regFields` builder — `M8086JitGenericityTests.cs:51-68`).

---

## What the recon CONFIRMED (file:line — load-bearing, verified against `main` @ `5eabddc`)

| # | Fact | Evidence |
|---|---|---|
| C1 | `_regFields` is a `Dictionary<string, FieldInfo>` built per-compile from `target.RegisterNames`, keeping only names for which `target.CpuType.GetField(name)` is non-null. Pair-view properties are SKIPPED by design (the "5-3b owns them" comment). | `BlockCompiler.cs:48` (field decl), `:96-107` (build loop + the skip comment) |
| C2 | The name→`FieldInfo` resolver is `RegField(string name)` — throws `EmulationException` if the name is absent from `_regFields`. The byte read/write idiom is `Ldarg_0; Ldfld, RegField(name)` (read) and `Ldarg_0; <value>; Conv_U1; Stfld, RegField(name)` (write). | `BlockCompiler.cs:530-533` (`RegField`); `BlockCompiler.Emit.cs:300-304` (`EmitLoadRegOrA`), `EmitLoad` write idiom (`BlockCompiler.Emit.cs:270-286`) |
| C3 | The Z80 8-bit halves are real `public byte` fields (`A,F,B,C,D,E,H,L`, the shadow `A_..L_`, `I,R,IXh,IXl,IYh,IYl`). The 16-bit pairs `IX,IY,AF,BC,DE,HL` + shadows are composed **properties** `get => (ushort)((hi<<8)\|lo); set { hi=(byte)(value>>8); lo=(byte)value; }`. `SP,PC,WZ` are the only real `ushort` **fields**. | generated `…Z80Cpu.g.cs:30-67` |
| C4 | The Z80 `RegisterNames` array (35 entries) DOES include the pair-view names (`"AF","BC","DE","HL","IX","IY","AF_","BC_","DE_","HL_"`) — so `_regFields` iterates over them and skips them (GetField null). The map a helper needs to consult is `target.RegisterNames` + `target.CpuType` (already injected). | `…Z80Cpu.g.cs:66` (`RegisterNames`); `Z80JitGenericityTests.cs:74-80` (the skip is asserted) |
| C5 | `EmitContext` (`ctx`) exposes `Il` (the `ILGenerator`) + named `LocalBuilder` scratch slots (`EaLocal, DataLocal, LoLocal, HiLocal, TmpInt, …`). The compose/decompose helper can stage halves through `LoLocal`/`HiLocal` or compute inline on the IL stack with no new local. | `EmitHelpers.cs` (the `EmitContext` definition; locals referenced throughout `BlockCompiler.*`) |
| C6 | There is an existing JIT genericity test fixture for the Z80 (`Z80JitGenericityTests.cs`) that builds a `BlockCompiler<Z80Cpu>` directly and inspects it — the natural home for the round-trip unit test. The 6502 equivalent (`Mos6502JitTomHarteTests.cs:114-126`) shows the `NewCompiler` + `Compile` + assert idiom. | `Z80JitGenericityTests.cs:53-97` |

**Net:** the register-name introspection (`target.RegisterNames` + `target.CpuType`) is already injected into
`BlockCompiler`; the half-field `FieldInfo`s are already discoverable. The helper is a small, self-contained
addition that reuses every existing seam — no new constructor argument, no generator touch, no descriptor change.

---

## The staged outline (one line each)

- **Task 1** — Add a per-compile **pair-view map** (`_regPairFields`: name → `(FieldInfo hi, FieldInfo lo)`)
  built alongside `_regFields`, plus a real-`ushort`-field set, so a pair name resolves to either two byte
  fields or one ushort field.
- **Task 2** — Add `EmitLoadReg16(EmitContext, string name)` — pushes the named 16-bit register's value (as
  `int`, 0..0xFFFF) onto the IL stack.
- **Task 3** — Add `EmitStoreReg16(EmitContext, string name)` — pops an `int` off the IL stack into the named
  16-bit register (compose into two halves, or `Conv_U2; Stfld` for a real ushort field).
- **Task 4** — The JIT unit test: round-trip every Z80 pair (write a known value via the helper into a real
  `Z80Cpu`, read it back, assert equality) — the gate (no throughput claim).
- **Task 5** — Build + test green; confirm no existing emit arm or test moved (the helper is unreferenced by
  any descriptor yet, so 6502/Z80 numbers are untouched).

---

## Task 1 — The pair-view register map (resolve a 16-bit name to its halves or its ushort field)

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.cs` (add the field + the build loop next to `_regFields`)

The existing `_regFields` build (`:96-107`) iterates `target.RegisterNames` and keeps the field-backed names.
Add a SECOND map for the field-less pairs, plus a set for the real `ushort` fields, derived from the same
`target.RegisterNames` introspection. A name is classified by reflection: if `GetField(name)` is a real
`ushort` field it goes in `_regWideFields`; else if both halves (the convention below) exist as `byte`
fields it goes in `_regPairFields`; else it is a property-only pair the helper composes from its halves.

**The half-name convention** (verified against `…Z80Cpu.g.cs:30-67`): a Z80 pair view is two adjacent
8-bit halves whose names are the standard register letters — `AF→(A,F)`, `BC→(B,C)`, `DE→(D,E)`, `HL→(H,L)`,
`IX→(IXh,IXl)`, `IY→(IYh,IYl)`, and the shadow set `AF_→(A_,F_)`, …, `HL_→(H_,L_)`. To avoid hard-coding a
per-CPU table (Decision 2: keep the register file data-driven), resolve halves by a **declared mapping the
JIT target can expose**, falling back to a name-decomposition heuristic. The lowest-risk M6 shape (no
`IJitTarget` change required) is a static decomposition table the helper owns, keyed by the pair name — the
pairs are a small fixed ISA fact, and adding a CPU's pairs is a one-line table entry, NOT a generator change.

- [ ] **Step 1:** Add the fields beside `_regFields` (`BlockCompiler.cs`, in the fields region ~`:48`):

```csharp
    // PR-0 (M6): the WIDE (16-bit) register file. Two shapes a structured CPU presents:
    //   (a) a real ushort FIELD (the Z80's SP/PC/WZ; the 8086's IP) — direct Ldfld/Stfld.
    //   (b) a field-less pair-view PROPERTY (the Z80's AF/BC/DE/HL/IX/IY + shadows; the 8086's
    //       AX/BX/CX/DX) over two byte HALF-fields — the emit arm composes hi<<8|lo / decomposes.
    // _regFields (the 8-bit map) SKIPS the (b) pairs by design (:96-107); these two members are how
    // an emit arm reaches them. Built per-compile from the same target.RegisterNames + target.CpuType
    // introspection — no generator change, no new ctor arg (Decision 2: the register file stays data).
    private readonly System.Collections.Generic.Dictionary<string, FieldInfo> _regWideFields;
    private readonly System.Collections.Generic.Dictionary<string, (FieldInfo Hi, FieldInfo Lo)> _regPairFields;
```

- [ ] **Step 2:** Add the static half-name decomposition table + the build loop, in the constructor right
  after the `_regFields` build (`BlockCompiler.cs` ~`:107`):

```csharp
        // PR-0: build the wide-register maps from the same introspection. A name that GetField resolves
        // to a ushort field is a direct wide field; a name in PairHalves whose two halves are byte fields
        // is a composed pair-view. (Names absent from both stay 8-bit-only, exactly as before.)
        _regWideFields = new System.Collections.Generic.Dictionary<string, FieldInfo>(System.StringComparer.Ordinal);
        _regPairFields = new System.Collections.Generic.Dictionary<string, (FieldInfo, FieldInfo)>(System.StringComparer.Ordinal);
        foreach (string name in target.RegisterNames)
        {
            if (target.CpuType.GetField(name) is { } wf && wf.FieldType == typeof(ushort))
            {
                _regWideFields[name] = wf;                       // SP/PC/WZ (Z80), IP (8086) — real ushort
            }
            else if (PairHalves.TryGetValue(name, out var halves)
                     && target.CpuType.GetField(halves.Hi) is { } hf
                     && target.CpuType.GetField(halves.Lo) is { } lf)
            {
                _regPairFields[name] = (hf, lf);                 // AF/BC/DE/HL/IX/IY + shadows; AX/BX/CX/DX
            }
        }
```

And the static table (place near the other static members at the top of `BlockCompiler.cs`):

```csharp
    // The hi/lo half-field names for each composed pair-view. A small, fixed ISA fact (NOT a generator
    // output) — adding a CPU's pairs is a one-line entry here, never a CpuEmitter.cs change. The halves
    // must be the byte FIELD names the generated CPU declares (verified: Z80 g.cs:30-67; 8086 g.cs AX..DX).
    private static readonly System.Collections.Generic.Dictionary<string, (string Hi, string Lo)> PairHalves =
        new(System.StringComparer.Ordinal)
        {
            // Z80 main set
            ["AF"] = ("A", "F"), ["BC"] = ("B", "C"), ["DE"] = ("D", "E"), ["HL"] = ("H", "L"),
            ["IX"] = ("IXh", "IXl"), ["IY"] = ("IYh", "IYl"),
            // Z80 shadow set
            ["AF_"] = ("A_", "F_"), ["BC_"] = ("B_", "C_"), ["DE_"] = ("D_", "E_"), ["HL_"] = ("H_", "L_"),
            // 8086 16-bit GP pair-views over byte halves (reused by PR-B; harmless to register now)
            ["AX"] = ("AH", "AL"), ["BX"] = ("BH", "BL"), ["CX"] = ("CH", "CL"), ["DX"] = ("DH", "DL"),
        };
```

> **Builder note:** confirm the exact half-field names against the generated CPU partial before committing —
> `…Z80Cpu.g.cs:30-67` for the Z80 (the `IXh/IXl/IYh/IYl` + `A_..L_` spellings are load-bearing), and the
> generated `…M8086Cpu.g.cs` for the 8086 `AH/AL/…` halves (PR-B consumes them; registering them now is a
> no-op until an 8086 emit arm references them). If a half name is absent (`GetField` null), the pair is
> simply not added — `EmitLoadReg16` then throws the clear "does not declare" error, never silently mis-emits.

---

## Task 2 — `EmitLoadReg16` (push a 16-bit register value onto the IL stack)

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.cs` (add the helper near `RegField`, ~`:533`)

The read idiom mirrors the existing 8-bit read (`Ldarg_0; Ldfld, RegField(name)`, `BlockCompiler.Emit.cs:300-304`)
but composes the pair when needed. Result on the stack is an `int` in `0..0xFFFF` (the same int-on-stack
convention the byte loads use, so callers stay uniform).

- [ ] **Step 1:** Add:

```csharp
    /// <summary>PR-0: push the named 16-bit register's value (as int, 0..0xFFFF) onto the IL stack.
    /// A real ushort field is a direct Ldfld; a composed pair-view is hi&lt;&lt;8 | lo over its two byte
    /// half-fields (the Z80 AF/BC/DE/HL/IX/IY shape; the 8086 AX/BX/CX/DX shape). Throws if the name is
    /// neither — the same fail-loud discipline as RegField.</summary>
    private void EmitLoadReg16(EmitContext ctx, string name)
    {
        ILGenerator il = ctx.Il;
        if (_regWideFields.TryGetValue(name, out var wf))
        {
            // (int)cpu.<ushort field>
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, wf);
            il.Emit(OpCodes.Conv_I4);            // ushort -> int (zero-extended)
            return;
        }
        if (_regPairFields.TryGetValue(name, out var p))
        {
            // (cpu.<hi> << 8) | cpu.<lo>
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, p.Hi);        // byte hi (loaded as int)
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shl);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, p.Lo);        // byte lo (loaded as int)
            il.Emit(OpCodes.Or);
            return;
        }
        throw new EmulationException(
            $"compiled descriptor names 16-bit register '{name}' which the CPU type declares neither as a "
          + "ushort field nor as a composable pair-view (PR-0 wide-register helper)");
    }
```

> **IL note:** `Ldfld` of a `byte` field pushes it as a zero-extended `int32` on the CLR stack, so `Shl`/`Or`
> compose correctly with no `Conv` needed; the result is a clean `0..0xFFFF` int. `Conv_I4` after the ushort
> `Ldfld` is technically a no-op (ushort already widens to int) but is kept explicit for stack-type clarity.

---

## Task 3 — `EmitStoreReg16` (pop an int off the IL stack into a 16-bit register)

**Files:**
- Modify: `src/CpuEmulator.Jit/BlockCompiler.cs` (add beside `EmitLoadReg16`)

The write decomposes into two byte stores for a pair-view (matching the generated property setter
`hi=(byte)(value>>8); lo=(byte)value;`, `…Z80Cpu.g.cs:30-67`), or a single `Conv_U2; Stfld` for a real
ushort field. The value to store is consumed from the top of the IL stack (an int in `0..0xFFFF`).

- [ ] **Step 1:** Add:

```csharp
    /// <summary>PR-0: pop an int (0..0xFFFF) off the IL stack into the named 16-bit register. A real ushort
    /// field is Conv_U2 + Stfld; a composed pair-view writes hi=(byte)(v&gt;&gt;8), lo=(byte)v — byte-identical
    /// to the generated property setter. Stages the value through ctx.TmpInt because both halves need it.</summary>
    private void EmitStoreReg16(EmitContext ctx, string name)
    {
        ILGenerator il = ctx.Il;
        if (_regWideFields.TryGetValue(name, out var wf))
        {
            // cpu.<ushort field> = (ushort)value   (stack: ..., value)
            il.Emit(OpCodes.Conv_U2);
            il.Emit(OpCodes.Stloc, ctx.TmpInt);   // stash the (already-truncated) value
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.TmpInt);
            il.Emit(OpCodes.Conv_U2);
            il.Emit(OpCodes.Stfld, wf);
            return;
        }
        if (_regPairFields.TryGetValue(name, out var p))
        {
            // stash value, then write both halves (stack: ..., value)
            il.Emit(OpCodes.Stloc, ctx.TmpInt);
            // cpu.<hi> = (byte)(value >> 8)
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.TmpInt);
            il.Emit(OpCodes.Ldc_I4_8);
            il.Emit(OpCodes.Shr_Un);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, p.Hi);
            // cpu.<lo> = (byte)value
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldloc, ctx.TmpInt);
            il.Emit(OpCodes.Conv_U1);
            il.Emit(OpCodes.Stfld, p.Lo);
            return;
        }
        throw new EmulationException(
            $"compiled descriptor names 16-bit register '{name}' which the CPU type declares neither as a "
          + "ushort field nor as a composable pair-view (PR-0 wide-register helper)");
    }
```

> **Builder note:** confirm `ctx.TmpInt` exists as an `int` `LocalBuilder` slot in `EmitContext`
> (`EmitHelpers.cs`). If the only scratch int slot is named differently (e.g. `TmpInt32` / `DataLocal`),
> use that one — the requirement is a single int local to hold the value across the two half-stores. Do NOT
> reuse `DataLocal`/`EaLocal` if a caller arm relies on them being live across this call; `TmpInt` is the
> safe dedicated scratch. If no free int local exists, add one to `EmitContext` (a one-line `DeclareLocal`
> addition in the context constructor) — that is the only `EmitContext` change this PR may need.

---

## Task 4 — The round-trip unit test (the gate)

**Files:**
- Modify: `tests/CpuEmulator.Tests/Jit/Z80JitGenericityTests.cs` (add the round-trip facts; it already builds
  a `BlockCompiler<Z80Cpu>` and constructs a real `Z80Cpu` — the right home)

Because the helpers are `private`, exercise them through a tiny compiled `DynamicMethod` the test drives, OR
(simpler + matching the existing genericity test idiom) add an `internal`-visible test seam that compiles a
one-shot method invoking `EmitLoadReg16`/`EmitStoreReg16` for a named pair and runs it against a live `Z80Cpu`.
The lowest-friction shape: a small `internal` method on `BlockCompiler<TCpu>` — `CompileReg16RoundTrip(string
name)` — that builds a `DynamicMethod(cpu)` doing `EmitStoreReg16(<const value>); EmitLoadReg16; ret` and
returns the readback, used only by the test (mark it with an XML comment "test seam", mirroring
`FallbackEmitCount`'s "test seam" framing at `BlockCompiler.cs:31`).

- [ ] **Step 1:** Add the internal test seam to `BlockCompiler.cs`:

```csharp
    /// <summary>Test seam (PR-0): compile a one-shot method that writes <paramref name="value"/> into the
    /// 16-bit register <paramref name="name"/> via EmitStoreReg16, reads it back via EmitLoadReg16, and
    /// returns the readback. Proves the wide-register helper round-trips for every pair-view + ushort field,
    /// independent of any op emit. No production caller — exists only for the PR-0 gate.</summary>
    internal int CompileReg16RoundTrip(string name, int value)
    {
        var dm = new System.Reflection.Emit.DynamicMethod(
            $"reg16_{name}", typeof(int), new[] { _target.CpuType }, _target.CpuType, skipVisibility: true);
        var il = dm.GetILGenerator();
        var ctx = NewTestContext(il);                 // a minimal EmitContext with the scratch locals declared
        il.Emit(OpCodes.Ldc_I4, value);
        EmitStoreReg16(ctx, name);
        EmitLoadReg16(ctx, name);
        il.Emit(OpCodes.Ret);
        var fn = (System.Func<TCpu, int>)dm.CreateDelegate(typeof(System.Func<TCpu, int>));
        return fn(_cpu);
    }
```

> **Builder note:** `NewTestContext(il)` stands in for "construct an `EmitContext` with `Il = il` and the
> scratch locals (`TmpInt`, etc.) declared." If the production code already has a context factory (e.g. the
> one `Compile` uses), reuse it; otherwise add a tiny internal factory that declares the same locals. Confirm
> the delegate signature against `_target.CpuType` — the round-trip needs the concrete `Z80Cpu` instance so
> the half-fields are real.

- [ ] **Step 2:** Add the test facts to `Z80JitGenericityTests.cs`:

```csharp
    [Theory]
    [InlineData("BC", 0x1234)]
    [InlineData("DE", 0xABCD)]
    [InlineData("HL", 0xBEEF)]
    [InlineData("AF", 0x55AA)]
    [InlineData("IX", 0x0FF0)]
    [InlineData("IY", 0xC3C3)]
    [InlineData("SP", 0xFFFE)]   // a real ushort field — the direct-Stfld path
    [InlineData("WZ", 0x8001)]
    public void Wide_register_helper_round_trips_every_Z80_pair(string name, int value)
    {
        var (z80, bus, opts) = NewZ80();                          // the fixture's existing builder
        var compiler = new BlockCompiler<Z80Cpu>(z80, Z80Cpu.JitTarget, bus, new Fastmem(bus, opts), opts);
        int readback = compiler.CompileReg16RoundTrip(name, value);
        Assert.Equal(value, readback);
        // And the underlying halves match the composed value (the property setter contract):
        Assert.Equal(value, z80.GetRegister(name));               // GetRegister reads the property/field
    }
```

> **Builder note:** `NewZ80()` is a placeholder for whatever the fixture already uses to build a `Z80Cpu` +
> bus + `JitOptions` (see `Z80JitGenericityTests.cs:53-97` for the existing construction). Reuse it verbatim.
> The second assertion (`z80.GetRegister(name)`) cross-checks the helper against the generated property
> getter — a deliberate oracle cross-check (the helper's compose must equal the CPU's own property).

---

## Task 5 — Build, test, and confirm nothing moved

- [ ] **Step 1:** `dotnet build src/CpuEmulator.Jit -warnaserror` clean; `dotnet build` (full) clean.
- [ ] **Step 2:** `dotnet test --filter "FullyQualifiedName~Z80JitGenericityTests"` green (the new round-trip
  theory passes for every pair + the two ushort fields).
- [ ] **Step 3:** Run the existing 6502 + Z80 JIT suites and confirm **no test moved** — the helper is
  referenced by NO descriptor and NO emit arm yet, so `Mos6502JitTomHarteTests` (the `ADC=0 fallbacks /
  BRK=1 fallback` probe, `:114-126`) and the Z80 all-fallback probe (`Z80JitGenericityTests.cs:83-97`,
  `FallbackEmitCount == 1` for `LD A,42h`) are UNCHANGED. This is the proof the PR is inert until PR-1
  wires it in.

---

## Test Plan

**Unit (the gate — no throughput claim, per ADR §5 / §8 PR-0 row):**
- `Wide_register_helper_round_trips_every_Z80_pair` — writes then reads each of `BC/DE/HL/AF/IX/IY` (composed
  pair-views) and `SP/WZ` (real ushort fields) through `EmitStoreReg16`/`EmitLoadReg16`, asserting the
  readback equals the written value AND equals the CPU's own `GetRegister` (the property-getter oracle).
- Build clean with `-warnaserror`.

**Parity / honesty gate (ADR §5):** **N/A by design** — PR-0 emits no op, so there is no tier-0-vs-tier-1
parity delta and no measured-data claim. The standing parity gates (ZEXALL/ZEXDOC, TomHarte JIT plane sweeps,
the 6502 ADC/BRK `FallbackEmitCount` probe) must remain **exactly as green as before** — the regression
check is "no existing test number moved," verified in Task 5 Step 3. This is the explicit ADR §8 PR-0 note:
*"a JIT unit test reads/writes each Z80 pair and round-trips; no measured-throughput claim (it emits no op yet)."*

---

## Dependencies

- **None.** `BlockCompiler.*` has no M5 collision (ADR §4: the JIT assembly is not M5-owned), so PR-0 can start
  immediately and runs in parallel with **PR-A** (8086 bench enablement, bench-only).
- **Unblocks:** PR-1 (Z80 `LD`) — PR-1's `LD BC,nn` / `LD (HL),r` / `LD HL,(nn)` arms call `EmitLoadReg16`/
  `EmitStoreReg16`. Also feeds PR-4 (68000 `D/A` 32-bit) and PR-B (8086 `AX/AL/AH`) — the same compose shape,
  extended to 32-bit / overlap respectively (a later widening of this helper, out of PR-0 scope).
- Touches NO `src/CpuEmulator.Generators/CpuEmitter.cs` — so it is OUTSIDE the §4 CpuEmitter.cs serialization
  rule entirely (pure JIT-runtime change).

---

## Definition of done

- `src/CpuEmulator.Jit/BlockCompiler.cs` carries `_regWideFields`, `_regPairFields`, the `PairHalves` static
  table, the `EmitLoadReg16` / `EmitStoreReg16` emit helpers, and the `CompileReg16RoundTrip` test seam.
- `Wide_register_helper_round_trips_every_Z80_pair` is green for all six composed pairs + `SP`/`WZ`.
- `dotnet build -warnaserror` + `dotnet test` green; **every pre-existing 6502/Z80 JIT test number unchanged**
  (the helper is inert — no descriptor references it yet).
- No `CpuEmitter.cs` edit, no generator change, no descriptor change, no measured-throughput claim.
- The PR body notes "PR-0 of the M6 arc (ADR 0011 §8) — the shared wide-register primitive PR-1 builds on;
  no op emit, no benchmark delta."
