# M6 PR-4a — word-granular `Discover` fetch-stream (the 68000 JIT-emit prerequisite)

> **STATUS: PLAN — preparatory doc.** The implementation touches `src/CpuEmulator.Jit/BlockCompiler.cs` (production
> JIT assembly) + adds an arm-selection counter probe + tests, so it lands on a branch + PR (per the workflow),
> NOT straight to main.
> **For agentic workers:** REQUIRED SUB-SKILL once scheduled — use `superpowers:subagent-driven-development` or
> `superpowers:executing-plans` to implement task-by-task. Steps use checkbox (`- [ ]`) syntax.
> **Read first (binding):**
> - `src/CpuEmulator.Jit/BlockCompiler.cs:283-321` — `Discover` (the ONE line, `:293`, that hardcodes the
>   byte-granular `BusFetchStream`).
> - `src/CpuEmulator.Core/Jit/M68000FetchStream.cs:69-77` — the **back-compat single-instruction constructor**
>   `M68000FetchStream(IAddressSpace bus, uint origin)` (the stateless decode-walk form — Seeds from the two
>   physical words at `origin`, behaves "like the old Read16-walk for the Length/decode contract").
> - `src/CpuEmulator.Cpus.M68000/…/M68000Cpu.g.cs:746-840` — the word-granular generated `Decode()`
>   (`uint operword = stream.NextUnit()` at `:748`; `int len = stream.UnitsConsumed * stream.UnitBytes` at `:833`).
> - `tests/CpuEmulator.Tests/TomHarte/M68000JitTomHarteTests.cs:105-161` — the **vacuous** MOVE-family sweep this
>   plan makes load-bearing (the "## Blocker" annotation at `:110-118` to be retired).
> - `tests/CpuEmulator.Tests/Jit/M68000JitGenericityTests.cs` — the FallbackEmitCount + MOVE-dest tests (the
>   `MOVE_to_An_postinc_predec` blocker annotation at `:167-177` to be retired).
> **DEPENDS ON / COMPOSES WITH PR-4** (`feat/m6-pr4-68000-descgen-move`, PR #77 OPEN) — see
> **"## How this composes with PR-4 (FOLD recommendation)"** below. This plan's emit-arm flip is *dead without
> PR-4's MOVE arm, and PR-4's arm is dead without this fix.*

---

## NO benchmark gate (owner policy, 2026-06-18)

Per-PR W2/W3 benchmark measurement is **OFF** for the entire 68000 arc (captured once at arc-end). **This plan has
NO "measured delta" gate.** This is a *correctness* fix (the emit path was never exercised); the merge
preconditions are the **fast correctness gates ONLY** (the regression-safety proofs + the dead-arm-now-live proof +
the real 68000 MOVE data-axis parity, all below).

---

## The blocker (root-caused by the PR-4 Builder)

`BlockCompiler.Discover` (`BlockCompiler.cs:293`) constructs a **byte-granular** `BusFetchStream` (`UnitBytes==1`,
`Read8`) for the decode walk. The 68000's generated `Decode()` is **word-granular** (`uint operword =
stream.NextUnit()` expects a 16-bit **big-endian** word, `M68000Cpu.g.cs:748`). Fed a byte stream, the 68000 reads
only the operword's HIGH byte (e.g. `0x32` from `MOVE.w D0,(A1)+` = `0x32C0`), mis-matches the field-op table →
`DescriptorFor` returns `Undefined`/`NeedsFallback` → **every 68000 block falls back to `inner.Step()`**. The MOVE
emit arm written in PR-4 (`EmitM68kMove`, `BlockCompiler.M68000.cs:402`) is **never dispatched**.

Proven by the PR-4 Builder: 0 emit-arm selections across 847 MOVE.w cases; the JIT parity sweep stays green even
with the emit IL deliberately broken — it is **vacuous** (interpreter-vs-interpreter via the all-fallback valve,
`M68000JitTomHarteTests.cs:110-118`).

### Why the 6502/Z80/8086 are unaffected today (the asymmetry)

Their generated `Decode()` walks read **bytes** — they were authored against a byte stream and consume
`stream.NextUnit()` as a byte (opcode + operand bytes), with `r.Length = UnitsConsumed * UnitBytes = UnitsConsumed
* 1`. So the byte-granular `BusFetchStream` is **correct** for them — and any change to it would be a regression.
The 68000 is the *only* CPU whose `Decode()` was authored word-granular (ADR 0004 Decision 1; `UnitBytes==2`,
big-endian). The fix is therefore inherently per-target: **pick the stream granularity by target**, change nothing
the byte CPUs see.

---

## The fix (DECISION: mechanism)

`Discover` must feed a fetch stream of the **right granularity per target CPU**: word-granular
(`M68000FetchStream`) for the 68000, byte-granular (`BusFetchStream`) for 6502/Z80/8086.

### The three candidate mechanisms (ranked — smallest blast radius first)

| Rank | Mechanism | Blast radius on byte CPUs | Verdict |
|------|-----------|---------------------------|---------|
| **① CHOSEN** | **Construct `M68000FetchStream` when `TargetIsM68000`, else `BusFetchStream`** — a one-line ternary in `Discover` using the *already-present* `TargetIsM68000` discriminator (`BlockCompiler.M68000.cs:46`). | **ZERO** — the `else` branch is byte-for-byte the current `new BusFetchStream(_bus, pc)`. No new seam, no new interface member, no generator change. | **Pick this.** |
| ② Key on `IFetchStream.UnitBytes` / a target-declared unit size | Requires a NEW `IJitTarget` member (`UnitBytes`/`UnitSize`) generated per CPU → touches `IJitTarget.cs` + `CpuEmitter.cs` + all 4 generated `JitTarget`s. Byte CPUs gain a new (==1) field. | Larger surface, generator change, regenerates all 4 g.cs files (diff noise on byte CPUs the regression gate must then re-bless). Reject. |
| ③ Per-`_target` fetch-stream factory (a `Func<AddressSpace,ushort,IFetchStream>` on `IJitTarget`) | Cleanest *abstraction*, but same generator-touch cost as ② **plus** a delegate-shaped seam (`AOT` review surface). | Over-engineered for a 1-of-4 special case. Reject for now; note as the generalization point if a 2nd word-granular CPU ever lands. |

**Why ① is lowest-blast-radius:** the discriminator (`TargetIsM68000 => _target.CpuType.Name == "M68000Cpu"`)
**already exists** and is **already** the routing key for the entire MOVE emit arm (`EmitInstruction`'s
`TargetIsM68000 && d.Class == JitOpClass.M68000Move` dispatch, `BlockCompiler.cs:443`). Reusing it for the
fetch-stream choice keeps the 68000-special-casing in exactly one named predicate, makes the `else` branch a
*literal no-op rewrite* of today's line (the strongest possible regression argument — `git diff` shows the byte
path unchanged in behavior), and adds **no** new public surface, **no** generator change, **no** g.cs regeneration.

### The constructor to use (the subtle correctness point)

Use the **stateless back-compat constructor** `new M68000FetchStream(_bus, pc)`
(`M68000FetchStream.cs:74-77`), **NOT** the live stateful queue the interpreter owns. The decode walk is a
debugger-view decode (it never executes, charges no cycle, issues no traced refill). The back-compat constructor
`Seed`s the queue from the two physical words at `origin` (`bus.Read16(origin)`, `bus.Read16(origin+2)`) and then
each `NextUnit()` advances via the untraced peek — value-identical to the old Read16-walk for the
Length/decode contract (its own XML doc, `:69-73`). This is exactly the same operword bytes `EmitM68kMove` already
reads as a compile-time constant via `_bus.Read16(pc)` (`BlockCompiler.M68000.cs:407`), so the discovered key and
the emitted arm see the identical operword. The stateful `Seed`/`Reseed`/refill machinery is irrelevant to
discovery (it models the *runtime* prefetch trace, the M4.5d timing axis the JIT data-axis gate ignores).

**Type compatibility (verified):** `M68000FetchStream(IAddressSpace bus, uint origin)`; `BlockCompiler._bus` is
`AddressSpace : IAddressSpace` (`AddressSpace.cs:8`) → implicit upcast, and `pc` (ushort) widens to `uint`. No
cast needed. (`BusFetchStream(IAddressSpace bus, ushort pc)` keeps the ushort `pc` — both compile from the same
call site.)

### The footprint-correction line stays correct for both granularities

`Discover` computes `int length = r.Length + Z80EmitOperandBytes(d)` (`:314`). For the 68000, `Z80EmitOperandBytes`
returns 0 (its switch only matches Z80 op-kinds; a 68000 descriptor's `Ops` carry no `JumpAbs`/`Ret`/etc. kinds and
`IsZ80FlowKind` is false), so `length == r.Length` — which is already the exact 68000 footprint
(`UnitsConsumed * 2`, operword + all extension words, `M68000Cpu.g.cs:833`). This matches the PR-4 invariant note
at `BlockCompiler.cs:307-313` ("NO 68000 footprint correction here"). **No change needed to the length math** — only
the stream the walk reads through. (Task 1 adds a regression assertion pinning this.)

---

## How this composes with PR-4 (FOLD recommendation)

**RECOMMENDATION: FOLD this fix into the PR-4 branch (`feat/m6-pr4-68000-descgen-move`, PR #77), do NOT ship a
standalone PR-4a.** Reasoning:

1. **They are functionally inseparable.** The emit arm is dead code without this fix; this fix has nothing to emit
   without the arm. Shipping them apart means PR-4 merges with a *provably-vacuous* parity gate (the very thing
   PR-4's own annotations flag as a defect, `M68000JitTomHarteTests.cs:110`), and PR-4a merges with no in-tree arm
   to exercise on its own branch base.
2. **The clean reviewable regression proof needs both halves in one diff.** The binding constraint (below) is "the
   byte CPUs are byte-identical AND the 68000 emit arm is now LIVE." That single claim is only checkable in a tree
   that has *both* the word-stream fix and the MOVE arm. Folded, PR #77's CI is the complete proof in one place;
   split, the reviewer must mentally compose two branches.
3. **It retires PR-4's own blocker annotations in the same PR that introduced them** — no window where `main` (or a
   merged PR-4) carries tests documented as "BLOCKED / not load-bearing."

**Mechanics of the fold:** the PR-4 Builder cherry-picks / applies Tasks 1-5 of this plan onto
`feat/m6-pr4-68000-descgen-move`, deletes the "## Blocker" / "BLOCKED" annotation blocks (Task 6), updates PR #77's
description to drop the blocker caveat and add the dead-arm-now-live evidence, and re-runs the gates. PR #77 then
merges as a *complete, non-vacuous* PR-4. **This planning doc + the queue/task row remain the PR-4a record** (the
fix has its own identity for traceability) even though the *code* lands inside PR #77.

> **Fallback (if the Coordinator prefers a standalone PR-4a):** ship Tasks 1-6 as PR-4a branched off `main`, then
> **rebase PR #77 onto PR-4a's merge** so #77's CI runs the real (non-vacuous) parity. This is strictly more
> process for the same net diff and leaves a brief vacuous-PR-4 window if #77 merges first — hence not recommended.
> Either way the **task content is identical**; only the branch topology differs.

---

## Objective

Make `BlockCompiler.Discover` feed the 68000 a **word-granular** `M68000FetchStream` so the generated word-decode
matches the table, `DescriptorFor` returns the real MOVE/MOVEA/MOVEQ descriptors, and `EmitM68kMove` **dispatches
at runtime** — turning the PR-4 MOVE-family JIT parity gate from *vacuous* (interpreter-vs-interpreter) into a
*real* emitted-IL-vs-interpreter gate — **with the 6502/Z80/8086 byte-granular paths completely unaffected.**

---

## CRITICAL — the binding design constraint (regression safety)

The byte-granular CPUs (6502/Z80/8086) **must be byte-for-byte unaffected**: same `Discover` behavior, same
descriptors, same `FallbackEmitCount`, same emitted blocks. The mechanism guarantees this *structurally* (the
`else` branch is the literal current line), and the Test Plan **proves** it (Tasks 2-3). Only the 68000 path
changes — from broken-byte-stream (100% fallback) to working-word-stream (MOVE-family emits).

### The thing to be MOST careful about (the one real risk)

**Could the byte-granular decode walks secretly depend on the *current* `BusFetchStream` instance/behavior in a way
the ternary perturbs?** Audited — the answer is **no**, and here is the evidence the implementer must re-confirm
(Task 2's assertions encode it):

- **The `else` branch is identical.** `Discover` for a non-68000 target executes
  `new BusFetchStream(_bus, pc)` — character-for-character today's line. The IL of the byte path is unchanged.
- **`Discover` has exactly ONE production caller of the stream constructor** (`BlockCompiler.cs:293`) — verified by
  grep; there is no second byte-stream construction site to drift.
- **No byte CPU's `Decode()` reads `UnitBytes`/`UnitsConsumed` in a way that differs by stream TYPE** — they
  multiply `UnitsConsumed * UnitBytes` with `UnitBytes==1`; the ternary never hands them anything but a
  `BusFetchStream`, so `UnitBytes` is still 1 for them.
- **`SeekTo` is called on the result** (`:318`). `M68000FetchStream` exposes no `SeekTo` — but `Discover` only
  reaches `SeekTo` in the loop body, and for the byte CPUs the result is *still* a `BusFetchStream` (which has
  `SeekTo`). **Risk:** the 68000 branch must also work across the multi-instruction loop (block of >1 MOVE). See
  **Task 1's seek handling** — the 68000 walk re-seeds per instruction differently; we construct a *fresh*
  `M68000FetchStream(_bus, pc)` at the top and, for the 68000, **re-construct per advance** rather than `SeekTo`
  (the stateless decode-walk constructor re-Seeds from the new `pc`), because `M68000FetchStream` has no `SeekTo`.
  This is the single non-trivial code shape in the fix and Task 1 spells it out with literal code.

---

## Tasks

### Task 1 — `Discover`: per-target fetch-stream granularity (the fix)

- [ ] In `src/CpuEmulator.Jit/BlockCompiler.cs`, change the **stream construction + per-instruction reposition** in
  `Discover` so the 68000 gets a word-granular `M68000FetchStream` and every other CPU keeps the byte-granular
  `BusFetchStream` — with **zero behavioral change to the byte path**.

The current loop (`:290-321`) constructs the stream once and `SeekTo`s it per instruction. `M68000FetchStream` has
**no `SeekTo`** (it is a stateful queue; its only reposition primitives — `Seed`/`Reseed`/`SeedPeek` — are the
runtime ones), and its **decode-walk** form is the stateless back-compat constructor. So the cleanest shape that
serves both is: **an `IFetchStream`-returning local factory** that builds a *fresh* stream positioned at a given
`pc`, called once up front and again per advance. For the byte CPU this re-construct is identical in effect to the
old `SeekTo` (a `BusFetchStream` constructed at `pc` has `_origin==pc`, `_offset==0` — exactly what `SeekTo(pc)`
produces). For the 68000 it re-Seeds the queue from `pc` (the correct per-instruction decode-walk start).

Replace the method body's stream lines with:

```csharp
public System.Collections.Generic.List<(ushort Pc, OpcodeDescriptor D, int Length)> Discover(ushort pc)
{
    var run = new System.Collections.Generic.List<(ushort, OpcodeDescriptor, int)>();
    // M6 PR-4a: the decode-walk fetch stream is per-target GRANULAR. The 6502/Z80/8086 generated Decode() walks
    // are BYTE-granular (BusFetchStream, UnitBytes==1, Read8) — authored against a byte stream. The 68000's
    // generated Decode() is WORD-granular (M68000FetchStream, UnitBytes==2, big-endian — it reads
    // `uint operword = stream.NextUnit()` as a 16-bit word, M68000Cpu.g.cs:748). Fed the byte stream the 68000
    // read only the operword's HIGH byte, mis-matched the field-op table, and DescriptorFor returned
    // Undefined/NeedsFallback — so EVERY 68000 block fell back and the MOVE emit arm never dispatched (the PR-4
    // blocker). The ternary keys on the SAME TargetIsM68000 discriminator that already routes the MOVE arm
    // (EmitInstruction, BlockCompiler.cs). The non-68000 branch is byte-for-byte the pre-PR-4a construction, so
    // the byte-granular CPUs see an IDENTICAL Discover (proven by their empty-diff descriptor tables + unchanged
    // FallbackEmitCount + green JIT sweeps — the PR-4a regression gate).
    IFetchStream NewStream(ushort at) => TargetIsM68000
        ? new M68000FetchStream(_bus, at)   // word-granular: Seeds the queue from the two physical words at `at`
        : new BusFetchStream(_bus, at);     // byte-granular: == the pre-PR-4a `new BusFetchStream(_bus, pc)`
    IFetchStream stream = NewStream(pc);
    for (int i = 0; i < _opts.BlockLengthCap; i++)
    {
        DecodeResult r = _target.Decode(stream);        // J3: the per-CPU decode seam
        OpcodeDescriptor d = _target.DescriptorFor(r.OperationKey);
        int length = r.Length + Z80EmitOperandBytes(d); // 68000: Z80EmitOperandBytes==0 (no Z80 op-kinds) → ==r.Length
        run.Add((pc, d, length));
        if (d.EndsBlock) break;
        pc = unchecked((ushort)(pc + length));           // advance by the FULL footprint
        // M6 PR-4a: reposition at the next instruction. The byte stream supports in-place SeekTo; the word stream
        // (a stateful queue with no SeekTo) is re-CONSTRUCTED fresh at the new pc — its stateless decode-walk ctor
        // re-Seeds the queue from `pc`, the correct per-instruction decode start (the runtime Seed/Reseed/refill
        // machinery is irrelevant to the never-executing discovery walk). Re-constructing the BYTE stream would be
        // equivalent to SeekTo, but we keep SeekTo on the byte path so its IL is unchanged from pre-PR-4a.
        if (stream is BusFetchStream bfs) bfs.SeekTo(pc);
        else stream = NewStream(pc);                     // 68000: fresh word-granular stream re-Seeded at pc
    }
    return run;
}
```

- [ ] Confirm the `using CpuEmulator.Core.Jit;` for `M68000FetchStream` + `IFetchStream` is present at the top of
  `BlockCompiler.cs` (it is — `BusFetchStream`/`IFetchStream` already resolve there; `M68000FetchStream` lives in
  the same `CpuEmulator.Core.Jit` namespace). If a build error reports `M68000FetchStream` unresolved, add the
  using; otherwise no using change.

> **Note on `SeekTo` retention (regression-safety detail):** keeping `bfs.SeekTo(pc)` on the byte branch (rather
> than re-constructing the byte stream too) means the byte CPUs' `Discover` loop body is *behaviorally identical*
> to pre-PR-4a — same `BusFetchStream` instance reused across instructions, same `SeekTo` resets. This is the
> minimal-perturbation choice the regression gate (Task 2) leans on.

### Task 2 — Regression proof: the byte-granular CPUs are byte-identical

- [ ] Add a focused test class `tests/CpuEmulator.Tests/Jit/WordGranularDiscoverRegressionTests.cs` that pins the
  byte CPUs' `Discover` is unperturbed. This is the *binding-constraint* proof, separate from the (already green)
  sweeps so a regression names itself.

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Cpus.M8086;
using CpuEmulator.Jit;
using Xunit;

namespace CpuEmulator.Tests.Jit;

/// <summary>M6 PR-4a: the BINDING regression gate. PR-4a changes BlockCompiler.Discover to feed the 68000 a
/// WORD-granular M68000FetchStream while the 6502/Z80/8086 keep the BYTE-granular BusFetchStream. This pins that
/// the byte-granular CPUs are BYTE-FOR-BYTE unaffected: the discovered run (pc, key, computed length per op) is
/// identical to a byte-stream walk, the stream the walk reads is STILL a BusFetchStream (UnitBytes==1), and
/// FallbackEmitCount is unchanged. The 68000 path is proven LIVE separately (M68000JitTomHarteTests, the dead-arm
/// counter). If this class ever goes red, the byte CPUs regressed — the one thing PR-4a must never do.</summary>
public class WordGranularDiscoverRegressionTests
{
    // 6502: LDA #$01 ; STA $10 ; NOP (mixed lengths: 2,2,1) — pins the COMPUTED length per op is unchanged.
    [Fact]
    public void Mos6502_discover_is_byte_granular_and_unchanged()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16, endianness: Endianness.Little);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0200, 0xA9); space.Write8(0x0201, 0x01);   // LDA #$01
        space.Write8(0x0202, 0x85); space.Write8(0x0203, 0x10);   // STA $10
        space.Write8(0x0204, 0xEA);                               // NOP
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Mos6502Cpu>(
            new Mos6502Cpu(space), Mos6502Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        var run = compiler.Discover(0x0200);
        // The computed lengths are the 6502 byte-stream footprints — unchanged by PR-4a (the ternary's else branch).
        Assert.Equal(2, run[0].Length);   // LDA #imm
        Assert.Equal(2, run[1].Length);   // STA zp
        Assert.Equal(1, run[2].Length);   // NOP
        Assert.Equal(0x0200, run[0].Pc);
        Assert.Equal(0x0202, run[1].Pc);
        Assert.Equal(0x0204, run[2].Pc);
    }

    // Z80: LD B,$05 (2) ; ADD A,B (1) ; HALT (1, ends block) — an EMITTED LD + ALU + the fallback terminator.
    // Pins FallbackEmitCount is EXACTLY the HALT (1), identical to pre-PR-4a (the byte path emits unchanged).
    [Fact]
    public void Z80_discover_and_fallback_count_are_unchanged()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16, endianness: Endianness.Little);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        space.Write8(0x0100, 0x06); space.Write8(0x0101, 0x05);   // LD B,$05
        space.Write8(0x0102, 0x80);                               // ADD A,B
        space.Write8(0x0103, 0x76);                               // HALT (ends block, falls back)
        var opts = new JitOptions();
        var compiler = new BlockCompiler<Z80Cpu>(
            new Z80Cpu(space), Z80Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        var run = compiler.Discover(0x0100);
        Assert.Equal(0x0100, run[0].Pc);
        Assert.Equal(2, run[0].Length);          // LD B,n footprint (opcode + imm)
        Assert.Equal(0x0102, run[1].Pc);
        Assert.Equal(0x0103, run[2].Pc);
        compiler.Compile(0x0100);
        Assert.Equal(1, compiler.FallbackEmitCount);   // exactly the HALT — the LD + ADD emitted (unchanged)
    }

    // 8086: MOV AL,imm8 (2) ; NOP (1) ; HLT (1, ends block). Pins the 8086 byte-granular run is unchanged.
    [Fact]
    public void M8086_discover_is_byte_granular_and_unchanged()
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 20, endianness: Endianness.Little);
        space.MapMemory(0x00000, new byte[0x100000], writable: true);
        space.Write8(0x0200, 0xB0); space.Write8(0x0201, 0x01);   // MOV AL,1
        space.Write8(0x0202, 0x90);                               // NOP
        space.Write8(0x0203, 0xF4);                               // HLT (ends block)
        var opts = new JitOptions();
        var compiler = new BlockCompiler<M8086Cpu>(
            new M8086Cpu(space), M8086Cpu.JitTarget, space, new Fastmem(space, opts), opts);
        var run = compiler.Discover(0x0200);
        Assert.Equal(0x0200, run[0].Pc);
        Assert.Equal(2, run[0].Length);   // MOV AL,imm8
        Assert.Equal(0x0202, run[1].Pc);
        Assert.Equal(0x0203, run[2].Pc);
    }
}
```

- [ ] **VERIFY before/after symmetry against `main`:** confirm each of these three `[Fact]`s is **already green on
  the pre-PR-4a tree** (they assert the *current* byte behavior). They must pass identically before and after the
  Task 1 change — that *is* the "byte CPUs unaffected" proof. (If the 8086 opcodes/lengths above don't match the
  real table, adjust the literals to the actual 8086 footprints — the assertion is "unchanged by PR-4a", so pin
  whatever the pre-PR-4a `Discover` returns.)

> **Note on the per-CPU genericity suites already in tree:** `Mos6502JitTomHarteTests`, `Z80JitTomHarteTests`,
> `M8088JitTomHarteTests`, `M8086JitGenericityTests`, `Z80JitGenericityTests` (with their FallbackEmitCount
> assertions) and `DiscoverComputedLengthTests`/`BlockCompilerTests`/`JitOptionsTests` (which all call `Discover`)
> are the *broad* regression net — they must stay green unchanged. Task 2's new class is the *named, focused* pin
> so a byte-CPU regression is unambiguous.

### Task 3 — Regression proof: the generated descriptor tables are empty-diff

- [ ] Confirm (no code change — a `git`/build check captured in the PR description) that **no generated file
  changes**: PR-4a touches only `BlockCompiler.cs` (+ the new arm-counter, Task 4 + tests). Run a clean rebuild and
  assert `git diff --stat` shows **zero** lines in any `**/obj/generated/**/*.g.cs` and zero in the committed
  6502/Z80/8086 spec/descriptor sources. This is the "generated descriptor tables empty-diff" gate (the same
  tripwire PR-4 uses for the byte CPUs). PR-4a makes **no** generator change, so this is expected — document the
  empty diff explicitly in the PR body.

### Task 4 — The arm-selection counter (make the dead-arm-now-live proof reproducible + committed)

The PR-4 Builder used an *ad-hoc* compile-time counter to confirm `EmitM68kMove` was selected 0 times
(`M68000JitTomHarteTests.cs:115`). Promote it to a **committed, asserted** counter so the dead-arm-now-live flip is
a permanent gate, not a one-off observation.

- [ ] In `src/CpuEmulator.Jit/BlockCompiler.cs`, add an internal compile-time selection counter alongside the
  existing `CompileCount` / `FallbackEmitCount` seams (so the test seam style is consistent):

```csharp
/// <summary>M6 PR-4a: a per-instance count of how many times an M68000Move row was DISPATCHED to EmitM68kMove
/// across this compiler's Compile calls (the arm-selection probe). Pre-PR-4a this was always 0 — the byte-stream
/// decode mis-matched the table so no MOVE descriptor ever reached the emit switch (the dead-arm blocker). A test
/// asserts it is &gt; 0 after a MOVE block compiles, so the 68000 MOVE parity gate is proven NON-vacuous (the emit
/// IL actually ran), not interpreter-vs-interpreter. Distinct from FallbackEmitCount (which resets per Compile);
/// this ACCUMULATES across Compiles so a sweep can assert one positive total.</summary>
internal int M68kMoveEmitSelections { get; private set; }
```

- [ ] Increment it at the MOVE dispatch site. Locate the emit switch arm that PR-4 added
  (`BlockCompiler.cs:461`, `case JitOpClass.M68000Move: EmitM68kMove(ctx, pc, d); break;`) and bump the counter
  there:

```csharp
case JitOpClass.M68000Move:
    M68kMoveEmitSelections++;   // M6 PR-4a: the dead-arm-now-live probe (asserted > 0 in the non-vacuous gate)
    EmitM68kMove(ctx, pc, d);
    break;
```

> If the exact switch shape differs once PR-4's code is on the working branch, increment at the *single* point
> immediately before `EmitM68kMove` is invoked from the class dispatch — the invariant is "one increment per
> M68000Move row that reaches the arm."

- [ ] Add the dead-arm-now-live assertion to `tests/CpuEmulator.Tests/Jit/M68000JitGenericityTests.cs` (it already
  owns the `NewM68k()` helper + the FallbackEmitCount theory):

```csharp
/// <summary>M6 PR-4a: the DEAD-ARM-NOW-LIVE gate. Pre-PR-4a the byte-granular Discover mis-decoded every 68000
/// op, so EmitM68kMove was NEVER selected (0 dispatches across 847 MOVE.w cases — the PR-4 Builder's finding) and
/// the MOVE parity sweep was vacuous (interpreter-vs-interpreter via the all-fallback valve). With the
/// word-granular Discover (PR-4a) the MOVE descriptor matches, reaches the emit switch, and EmitM68kMove
/// DISPATCHES — so M68kMoveEmitSelections is &gt; 0. This is the un-fakeable proof the 68000 MOVE JIT parity is
/// now REAL emitted-IL-vs-interpreter, not a degenerate tautology.</summary>
[Fact]
public void M68000_MOVE_arm_actually_dispatches_after_PR4a()
{
    if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;   // emit-only proof
    var (_, bus, compiler) = NewM68k();
    bus.Write16(0x001000, 0x3200);   // MOVE.w D0,D1 (register-only EA — no ext words)
    bus.Write16(0x001002, 0x4E71);   // NOP — the block-ending fallback
    compiler.Compile(0x1000);
    Assert.True(compiler.M68kMoveEmitSelections > 0,
        "EmitM68kMove was never selected — Discover is still feeding the 68000 a byte-granular stream (the dead-arm blocker).");
}
```

- [ ] **Negative control (prove the counter can read 0 — non-vacuous probe):** assert a block of *only* a fallback
  68000 op (NOP) yields `M68kMoveEmitSelections == 0`, so a green positive case is meaningful:

```csharp
[Fact]
public void M68000_non_MOVE_block_selects_the_MOVE_arm_zero_times()
{
    if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported) return;
    var (_, bus, compiler) = NewM68k();
    bus.Write16(0x001000, 0x4E71);   // NOP only — falls back, no MOVE row
    compiler.Compile(0x1000);
    Assert.Equal(0, compiler.M68kMoveEmitSelections);
}
```

### Task 5 — Make the real 68000 MOVE data-axis parity gate load-bearing

- [ ] `tests/CpuEmulator.Tests/Jit/M68000JitGenericityTests.cs` — the `M68000_MOVE_block_emits_no_fallback_after_PR4`
  theory (`:114-133`) and the `MOVE_to_An_postinc_predec_writes_the_source_operand_not_the_advanced_address` theory
  (`:178-218`) are **now genuinely exercising emitted IL** (Discover dispatches the arm). No code change to the
  test bodies is needed — they already drive `JittedCpu.Run` and diff against the interpreter. **Add an assertion**
  to the post-inc/pre-dec theory that the arm actually dispatched (so it can never silently re-degrade to the
  fallback path):

```csharp
// (inside RunOne's throughJit branch, after jit.Run — expose the selection count via the JittedCpu's compiler,
//  OR re-compile the same entry through a BlockCompiler to read M68kMoveEmitSelections, mirroring Task 4.)
```

> **Implementation note for the worker:** the cleanest non-invasive form is to assert the *standalone* compile
> selection separately (as Task 4 does) and leave these data-axis theories as the pure parity diff. If
> `JittedCpu<M68000Cpu>` does not already surface its inner `BlockCompiler`, do **not** add a new public surface
> just for the assert — Task 4's dedicated `M68000_MOVE_arm_actually_dispatches_after_PR4a` already pins dispatch;
> these theories pin the *data result*. Together they are the complete non-vacuous proof. (If a single-test
> dispatch+data assertion is wanted, compile the same entry via a throwaway `BlockCompiler` to read the counter, as
> in Task 4 — no production-surface change.)

- [ ] `tests/CpuEmulator.Tests/TomHarte/M68000JitTomHarteTests.cs` — the `M68000JitMoveFamilyTests` sweep
  (`:119-161`) is the headline **real 68000 MOVE data-axis parity** gate (MOVE.b/.w/.l, MOVEA.w/.l, MOVEQ — the
  full DATA axis D0–D7/A0–A6/USP/SSP/SR/RAM, byte-identical JIT-vs-interpreter, NOT cycle/pc/prefetch per DECISION
  T). With PR-4a it runs **real emitted IL**. No body change — it becomes load-bearing automatically. (Task 6
  retires its "BLOCKED" annotation.)

### Task 6 — Retire the blocker annotations

- [ ] `tests/CpuEmulator.Tests/TomHarte/M68000JitTomHarteTests.cs:110-118` — delete the `<para><b>BLOCKED …</b>`
  block in `M68000JitMoveFamilyTests`'s XML doc; replace with a one-line note that PR-4a's word-granular Discover
  makes this a real emitted-IL gate (cite `M68kMoveEmitSelections > 0`).
- [ ] `tests/CpuEmulator.Tests/Jit/M68000JitGenericityTests.cs:167-177` — delete the `<para><b>BLOCKED …</b>` block
  in `MOVE_to_An_postinc_predec_…`'s XML doc; replace with the same one-line PR-4a note.
- [ ] Grep the repo for any remaining `## Blocker` / `BLOCKED` / "byte-granular BusFetchStream … falls back"
  narrative tied to this issue (`M68000JitTomHarteTests`, `M68000JitGenericityTests`, and any
  `docs/BUILDER_QUEUE.md` row if one exists by then) and update each to "resolved by PR-4a." Do **not** touch the
  PR-4 *plan* doc's historical record (`docs/superpowers/plans/2026-06-18-m6-pr4-68000-descgen-move.md`) — it
  describes PR-4's scope at the time; PR-4a is its own doc.

---

## Test Plan (the merge gate — fast correctness only, NO benchmark)

All gates run via `dotnet test`. The 680x0 SingleStep vectors gate behind `[M68000TomHarteTheory]` (skips when the
corpus is absent) — run with the vectors present (or `CPUEMULATOR_UAT=full` for the complete sweep).

### A. Regression-safety proofs (the BINDING constraint — the byte CPUs are byte-identical)

1. **`WordGranularDiscoverRegressionTests` (Task 2) — GREEN, before AND after.** The three byte-CPU facts assert
   the *current* byte-granular `Discover` run (pc + computed length per op) and Z80 `FallbackEmitCount == 1`. They
   pass identically on `main` and on the PR-4a tree — the direct "unaffected" proof.
2. **The byte-CPU JIT TomHarte parity sweeps stay GREEN, unchanged:** `Mos6502JitTomHarteTests`,
   `Z80JitTomHarteTests`, `M8088JitTomHarteTests` (the 8086/8088 JIT sweep). No new failures, no diff in
   executed-case counts.
3. **The byte-CPU `FallbackEmitCount` is unchanged:** `Z80JitGenericityTests` (all FallbackEmitCount theories) +
   `Mos6502JitTomHarteTests` / `M8086JitGenericityTests` stay green — same emitted/fallback split as `main`.
4. **The byte-CPU generated descriptor tables are EMPTY-DIFF (Task 3):** a clean rebuild leaves
   `git diff --stat` with **zero** lines under any `**/obj/generated/**/*.g.cs` and zero in the 6502/Z80/8086 spec
   sources — PR-4a makes no generator change. Documented in the PR body.
5. **The broad `Discover` callers stay green unchanged:** `BlockCompilerTests`, `DiscoverComputedLengthTests`,
   `JitOptionsTests` (all 6502-over-`Discover`) — no behavior change.

### B. The dead-arm-now-live proof (the parity is no longer vacuous)

6. **`M68000_MOVE_arm_actually_dispatches_after_PR4a` (Task 4) — GREEN:** `M68kMoveEmitSelections > 0` after a
   `MOVE.w D0,D1` block compiles. Pre-PR-4a this would be `== 0` (the dead arm). This is the un-fakeable flip.
7. **`M68000_non_MOVE_block_selects_the_MOVE_arm_zero_times` (Task 4) — GREEN:** a NOP-only block reads
   `M68kMoveEmitSelections == 0` — the counter is a real probe (it can read 0), so the positive case in #6 is
   meaningful.

### C. The real 68000 MOVE data-axis parity (now load-bearing emitted IL)

8. **`M68000JitGenericityTests.M68000_MOVE_block_emits_no_fallback_after_PR4` (Task 5) — GREEN, now REAL:** every
   MOVE/MOVEA/MOVEQ form (incl. the `(A1)+` / `-(A1)` memory-dest probes `0x32C0`/`0x3300`) compiles with
   `FallbackEmitCount == 1` (only the block-ending NOP) — and the MOVE now genuinely emitted (proven non-vacuous by
   B).
9. **`M68000JitGenericityTests.MOVE_to_An_postinc_predec_…` (Task 5) — GREEN, now REAL:** the memory-dest MOVE
   writes the **source operand** (not the advanced An) and advances A1 — JIT byte-identical to the interpreter on
   the written RAM word + A1. (Pre-PR-4a this was interpreter-vs-interpreter; now it diffs emitted IL vs the
   oracle.)
10. **`M68000JitMoveFamilyTests.Move_family_emitted_IL_is_data_axis_parity_green` (Task 5/6) — GREEN, now REAL:**
    the headline MOVE-family data-axis sweep (MOVE.b/.w/.l, MOVEA.w/.l, MOVEQ) — JIT final
    D0–D7/A0–A6/USP/SSP/SR/RAM byte-identical to the interpreter for every executed case (`executed > 0`, the
    vacuity guard). NOT cycle/pc/prefetch (DECISION T). This is the gate that was *vacuous* pre-PR-4a and is *real*
    now.

### D. The all-fallback 68000 sweep is unperturbed for the non-MOVE families

11. **`M68000JitSweepBase` partitions P0..P7 (the full data-axis sweep) stay GREEN:** every non-MOVE 68000 op still
    falls back (its descriptor is still `Undefined`/`NeedsFallback` — PR-4 only populated MOVE/MOVEA/MOVEQ), so the
    word-granular stream decodes them, finds no emittable descriptor, and falls back exactly as before — same
    data-axis parity. (Confirms the word-stream switch did not *break* the still-fallback families: their decode
    now reads the correct word key but maps to a fallback descriptor, ending the block as a single fallback op,
    identical final state.)

### E. Build / AOT cleanliness

12. **`AotCleanlinessTests` GREEN:** no `Reflection.Emit` leaked into the AOT surface (PR-4a adds none — the
    counter is a plain field increment).
13. **Full `dotnet build` + `dotnet test` GREEN** on the configured runner.

> **NO benchmark step.** Per the arc policy, W2/W3 is captured once at 68000-arc-end. PR-4a is a correctness fix;
> the cumulative emit delta (MOVE was dead, now live) folds into that single arc-end measurement.

---

## Files touched

| File | Change |
|------|--------|
| `src/CpuEmulator.Jit/BlockCompiler.cs` | Task 1: per-target fetch-stream in `Discover` (the fix). Task 4: `M68kMoveEmitSelections` counter + the increment at the MOVE dispatch arm. |
| `tests/CpuEmulator.Tests/Jit/WordGranularDiscoverRegressionTests.cs` | Task 2: NEW — the byte-CPU byte-identical regression gate. |
| `tests/CpuEmulator.Tests/Jit/M68000JitGenericityTests.cs` | Task 4: the dead-arm-now-live + negative-control facts. Task 6: retire the `MOVE_to_An_postinc_predec` BLOCKED annotation. |
| `tests/CpuEmulator.Tests/TomHarte/M68000JitTomHarteTests.cs` | Task 6: retire the `M68000JitMoveFamilyTests` BLOCKED annotation (the sweep body is already load-bearing). |

**No generator change. No g.cs regeneration. No 6502/Z80/8086 source change.** (The empty-diff gate, A4.)

---

## Risks & mitigations

- **R1 — a byte CPU secretly depends on the old `BusFetchStream` instance behavior.** *Mitigation:* the `else`
  branch is the literal current line; the byte path keeps `SeekTo` (same instance reused across instructions);
  Task 2 + the broad `Discover`-caller suites pin byte-identity before/after. **The single most-watched item.**
- **R2 — `M68000FetchStream` has no `SeekTo`, so the multi-instruction 68000 loop must re-construct per advance.**
  *Mitigation:* Task 1's literal code re-constructs the word stream at each `pc` (its stateless decode-walk ctor
  re-Seeds correctly); the still-fallback sweep (D11) and any multi-MOVE block exercise the loop.
- **R3 — the stateless decode-walk constructor reads two physical words at `pc` via `bus.Read16`; on the JIT's
  fastmem/tracing bus this must not differ from what `EmitM68kMove` reads.** *Mitigation:* `EmitM68kMove` already
  reads the operword via `_bus.Read16(pc)` — the identical bus + value. The decode walk never executes and charges
  no cycle; it only computes the key + length. Verified value-identical.
- **R4 — fold-vs-standalone topology confusion.** *Mitigation:* the FOLD recommendation is explicit; the task
  content is identical either way; only the branch base differs.

---

## Done when

- All 13 Test-Plan gates GREEN (A–E).
- `M68kMoveEmitSelections > 0` proven for a MOVE block; `== 0` for a non-MOVE block.
- The byte-CPU descriptor tables show an empty `git diff`; their FallbackEmitCount + JIT sweeps unchanged.
- The "BLOCKED" annotations retired in both test files.
- Folded into PR #77 (recommended) — PR #77's description updated to drop the blocker caveat and cite the
  dead-arm-now-live evidence — OR shipped as standalone PR-4a with #77 rebased onto it (fallback).
```