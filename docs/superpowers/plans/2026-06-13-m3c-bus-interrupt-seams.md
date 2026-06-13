# M3.2: Bus I/O-Space + Interrupt/Halted Seams — the last seam-generalization before the Z80 lands

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** generalize CpuEmulator's **I/O-space** and **interrupt/halted** seams so the Z80's
`IN`/`OUT`, `HALT`, and `IM 0/1/2`+NMI+`I`/`R` interrupt model become *expressible* — without adding
a single Z80 opcode. Concretely this milestone delivers three things, each scoped to its honest size:

1. **I/O-space micro-ops** `PortIn(reg)` / `PortOut(reg)` — new micro-ops that read/write the `Io`
   `AddressSpace` (the Z80's `IN`/`OUT`). The 6502 has none, so this is **purely additive**, proven by
   a synthetic test CPU that declares an `Io` space + a port op and exercises BOTH tiers (interpreter +
   JIT) hitting the `Io` space, never the program space. The class/mode matrix grows to admit a port
   class. The JIT fastmem is confirmed to NEVER fastmem the `Io` space (I/O is always a bus callout).

2. **A generic halted state** — a `Halt()` micro-op + a framework-level "halted" CPU state where the
   CPU idles (advancing cycles, not faulting) until an interrupt. Built generically so the Z80 `HALT`
   and the 68000 `STOP` share it. Resolves the recorded M2 carry-forward: today the `Machine.Run`
   no-progress guard would fire on a halted CPU. Synthetic proof.

3. **Interrupt-seam confirmation + the minimal generalization IM 0/1/2 actually needs.** The honest
   finding (derived below, §Ground truth C): the interrupt seam is **already almost entirely generic**
   — servicing is hand-written per-CPU (`Mos6502Cpu.TryServiceInterrupt`), the `InterruptPending` hook
   is per-CPU, `InterruptLine` is wired-OR/multi-source, and both tiers boundary-sample
   `InterruptPending` without knowing the policy. M3.2's interrupt work is therefore **mostly a
   documented confirmation + one small enabling change**: route the interrupt-acknowledge bus cycle
   (which Z80 `IM 0`/`IM 2` need — the device supplies a byte) through the same I/O-bus wiring item (1)
   adds, so a Z80 partial *can* implement IM 0/1/2 as CPU-internal logic. Proven by a synthetic
   non-6502-interrupt-model test (a vectored-from-a-table service in a synthetic partial). **No Z80 IM
   logic is implemented (M3.4).**

4. **Record M4 inputs (don't build):** the bus transaction surface stays additive (`Read16/32` +
   endianness = M4); the 3-bit IPL interrupt-line level = M4 interrupt-seam nudge. A short "deferred to
   M4" note, not code.

**Honest scope headline (stated up front, derived in §"Derived scope"):** **M3.2 is LIGHTER than
M3.1a (register file) and M3.1b (decode walk).** Two of its three items are not refactors: the I/O
micro-ops are *additive* (the `Io` `AddressSpace`, `AddressSpaceKind.Io`, and the `Machine`/
`MachineBuilder` multi-bus wiring already exist and are generic — built M1), and the interrupt seam is
*confirm-only* plus one small enabling change. Only the I/O micro-op emission + the halted state are
genuine new code, and both are small, well-contained, single-PR-sized additions. There is **no
6502-touching refactor** here at all: the 6502 declares no `Io` space, no port ops, and never halts, so
`Mos6502Spec.cs` is unchanged and the generated 6502 output is **byte-identical** (asserted, derived in
§Ground truth E).

**PR:** branch `feat/m3-bus-interrupt-seams` (base `main`, head `1b22c33`; **~1467 tests green** is the
controller-stated baseline — confirm the exact count at Task 0). This plan file is a preparatory doc
commit on that branch; the implementation tasks follow.

---

## Scope

**IN scope (the I/O + interrupt/halted dimensions, end to end):**

1. **The `PortIn`/`PortOut` micro-ops + an `IoPort` addressing mode** (Ground truth A). New `Op` records
   `PortInOp(string Target)` / `PortOutOp(string Source)`; new `Spec.PortIn`/`Spec.PortOut` factories;
   a new `AddrMode.IoPortImmediate` (the `(n)` form — an 8-bit port number operand) and
   `AddrMode.IoPortIndirect` (the `(C)` form — the port number comes from a register). The generator's
   class/mode matrix grows a **Port class** admitting exactly these modes. The interpreter bodies +
   JIT emit arms read/write the `Io` `AddressSpace`.

2. **The CPU↔I/O-bus wiring** (Ground truth A.4). The generated CPU consumes an *optional* second
   address space. The hand-written partial supplies `ReadIo`/`WriteIo` helpers (the I/O analogues of
   `ReadBus`/`WriteBus`) the generated port bodies call. The JIT's bus callout for a port op routes to
   a **second** `IAddressSpace` callout arm, distinct from the fastmem'd memory bus.

3. **The generic halted state** (Ground truth B). A `HaltOp` micro-op + `Spec.Halt()` factory; a
   `Halted` predicate the generated `Step` consults (a CPU is halted ⇒ `Step` idles one cycle instead
   of fetching); the halted latch cleared when an interrupt is serviced. The `Machine.Run` no-progress
   guard accommodates a halted-but-advancing CPU (it already advances ≥1 cycle/Step, so the guard does
   not fire — confirmed + pinned). The JIT dispatcher gets a halted fast path so a `HALT` does not
   busy-compile.

4. **The interrupt-acknowledge bus reachability** (Ground truth C). The one small enabling change: the
   hand-written partial can reach the `Io` bus (the same wiring item (2) adds), so a Z80-style partial's
   `IM 0`/`IM 2` service (which reads a byte the device places on the bus) is expressible. **The
   `InterruptPending`/`TryServiceInterrupt` seam itself is UNCHANGED** — it is already general enough
   (derived in Ground truth C). Documented as the confirmation it is.

5. **Two synthetic test CPUs** (Ground truth F): (F.1) a **port CPU** declaring an `Io` space + a
   `PortIn`/`PortOut` op, driven through both tiers, asserting the access lands on the `Io` bus not the
   program bus, and that fastmem never serves it; (F.2) a **halt + non-6502-interrupt CPU** declaring a
   `Halt()` op and a hand-written partial whose `TryServiceInterrupt` implements a *vectored-from-a-
   table* service (NOT the 6502's fixed vector), proving the seam expresses a non-6502 interrupt shape
   and that a halted CPU idles + wakes on the serviced interrupt.

6. **The M4 deferred-inputs note** (Ground truth H): a short, code-free record that the wide-bus
   surface (`Read16/32`, endianness) and the 3-bit IPL interrupt level are planned M4 growth.

**NOT in scope (stated so an implementer does not reach for it):**

- **ANY Z80 code.** No Z80 `IN`/`OUT`/`HALT`/`IM`/`DI`/`EI` opcodes, no `IFF1`/`IFF2`, no `I`/`R`
  registers, no real Z80 IM 0/1/2/NMI service logic. The forward value is proven by the 6502 staying
  green (byte-identical) + the two synthetic CPUs, exactly as the brief mandates. The Z80's interrupt
  service is M3.4 (interpreter).
- **ANY 68000/8086 code.** No `STOP`, no IPL level, no IVT. The halted state is built *generically* so
  68000 `STOP` reuses it (a design constraint), but no 68000 is built. The 3-bit IPL level and the IVT
  interrupt shape are recorded as M4/M5 (Ground truth H), not implemented.
- **The wide-bus surface** (`Read16`/`Read32`/`Write16`/`Write32`, endianness on `IAddressSpace`). The
  bus stays byte-only and additive — `Read16` is the load-bearing M4 decision (68000 brief §2, M4 open-
  question 1). Recorded as M4 growth (Ground truth H), not built. The Z80's 16-bit memory ops decompose
  into two `Read8`s (M3.4), so even the Z80 does not force a word bus.
- **A new interrupt-line *level* type.** `IInterruptLine` stays boolean (the Z80's `INT`/NMI are
  boolean). The 3-bit IPL level is the recorded M4 nudge (Ground truth H), not built.
- **The flag model** (`Flag` enum, `s_flagMembers`, the per-arch flag micro-op family). Untouched — a
  separate future chunk (M3.1a + M3.1b both deferred it; the Z80's `DAA`/parity/half-carry flag family
  is M3.4 work). `LD A,I`/`LD A,R` copying `IFF2` into `P/V` is M3.4.
- **Register aliasing / 16-bit ALU / the prefix DECODE structure.** Those are M3.1a (done), M3.1b
  (done), and M3.4 (Z80 interpreter). This plan touches NEITHER the register file NOR the decode walk.
- **JIT genericity J1** (making `BlockCompiler`/`BlockDelegate`/`JittedCpu` generic over the CPU type —
  retiring `typeof(Mos6502Cpu)`). Deferred to M3.5, exactly as M3.1b deferred it. The synthetic-CPU
  JIT proofs for the port op + halted state drive the generated `Decode`/emit primitives **directly**
  (the M3.1a/M3.1b precedent — see Ground truth F), not a second CPU type through the live
  `BlockCompiler`. The *6502* port-op/halt JIT arms cannot be exercised because the 6502 has no port op
  and never halts — so the JIT-arm correctness is proven by the synthetic direct-emit fixture + the
  unchanged 6502 JIT suite (byte-identical).

**Recorded deviations / departures this plan makes deliberately:**

- **The port op's JIT arm is EMITTED (a second-bus callout), not a fallback.** ADR Decision 4 says
  many Z80 ops are reasonable fallback candidates initially; the port op is *not* one of them — it is a
  trivial unconditional bus callout (the same shape as the MMIO arm of `EmitStoreByte`, just to a
  different `IAddressSpace`), so emitting it costs almost nothing and proves the "I/O is always a
  callout, never fastmem" rule by construction. Recorded: emitting the port arm (vs. falling back) is a
  deliberate choice because the arm is genuinely simple and the *point* of the milestone is to prove
  the never-fastmem rule in emitted IL. The synthetic port CPU's JIT proof asserts the emitted arm
  hits the `Io` bus.

- **The halted state is a generated predicate + a `Step` guard, NOT a new `ICpuCore` method.** A halted
  CPU is observable through the existing `Step`/`Run` contract (Step idles a cycle; `CycleCount`
  advances). I do not add `bool IsHalted` to `ICpuCore` — the halted state is a CPU-internal latch the
  hand-written partial owns (like the 6502's `_nmiPending`), surfaced to the generated `Step` through a
  `partial bool Halted` hook (the same partial-hook altitude as `InterruptPending`). Rationale: the
  monitor/harness do not need to *ask* "are you halted"; they need the CPU to keep advancing cycles and
  to wake on an interrupt, which the `Step` guard + the existing `InterruptPending` hook already give.
  Adding a public `IsHalted` would be speculative surface (no consumer). Recorded.

- **`AddrMode.IoPortImmediate`/`IoPortIndirect` are added to the SHARED `AddrMode` enum + the
  `s_addrModes` mirror + `JitMode` (the mirror-table tax M3.1b §11 flagged).** This is the same 3-edit
  cost every mode addition pays today; M3.1a killed it for *registers* but not modes. I pay the tax
  (3 synchronized edits) here rather than data-drive modes, because data-driving the mode enum is a
  separate, larger refactor explicitly scoped out of M3 (8086 brief §11 schedules it as future work).
  Recorded: paying the mode mirror tax is a deliberate scope line, not an oversight.

- **`PortInOp`/`PortOutOp` carry a register NAME (the J2 data-driven convention), not a `Reg`.** Same
  as every M3.1a-era op: the register arg is a string literal validated against the spec's `Registers`
  table (CPUGEN008). The Z80's `IN A,(n)` names `A`; `IN r,(C)` names any 8-bit register. Recorded for
  forward-consistency (no Z80 reg enum).

- **The synthetic interrupt CPU proves a NON-6502 interrupt SHAPE (vectored-from-a-table), not the real
  Z80 IM 2.** It is the minimum that demonstrates the seam is not 6502-fixed-vector-shaped. It does NOT
  implement `I`-register × table-index addressing, `IFF1`/`IFF2`, or the `EI` delay (all M3.4). The
  proof is "a hand-written partial can vector through a table it reads from the bus" — the
  expressibility requirement. Recorded scope honesty.

**ADR + brief links:**
- **ADR 0001 Decision 2** (`docs/architecture/0001-z80-second-architecture.md:182-232`) — the I/O
  address space: recommendation (A), a second `AddressSpace(Io, 16)`; the load-bearing JIT half: I/O is
  **never fastmemmed** — always a callout (`0001-…:216-224`); the genericity finding that the
  CPU↔bus wiring "currently bakes one bus" and generalizing to "a CPU declares the buses it owns" is a
  small real Core change (`0001-…:227-232`).
- **ADR 0001 Decision 5** (`docs/architecture/0001-z80-second-architecture.md:402-447`) — the interrupt
  model: recommendation (A), keep the hand-written-partial seam; **the seam is ALREADY generic** (built
  CPU-agnostic in M1 3b-ii, `0001-…:434-438`); the IM 0/2 interrupt-acknowledge read routes through the
  I/O bus wiring from Decision 2 (`0001-…:427-429, 436`); the `HALT` watch item — the `Run` loop must
  not busy-spin (`0001-…:445-447`); open-question 8 (`0001-…:720-723`).
- **ADR 0001 Decision 7, J6** (`0001-…:510`) — the interrupt boundary sample is already generic;
  `HALT` must not spin; the dispatcher may need a halted fast path.
- **68000 brief §"M3 NOW" items 9-10** (`docs/research/68000-architecture-analysis.md:785-790`) — build
  `HALT` as a GENERIC halted state the 68000 `STOP` reuses verbatim; the 3-bit IPL level is the one
  likely interrupt-seam contract growth M4 forces — record it as a planned finding. §2/§8
  (`:176-249`) — the wide-bus `Read16/32`+endianness surface is the M4 decision; keep M3 byte-only and
  additive (M4 open-q 1, `:806-812`).
- **8086 brief §10.2-10.3 + §6** (`docs/research/8086-architecture-analysis.md:516-568, 745-757,
  833-841`) — the 8086 **shares the I/O space** (`IN`/`OUT` to the separate space, "pre-paid" by this
  milestone, `:244-245`); the **interrupt-vector-table** model is a 3rd interrupt shape after the 6502
  fixed-vector + Z80 IM (`:537-568`), implementable in a partial's `TryServiceInterrupt` "exactly as
  the 6502 does — no Core change" (`:568`); do NOT pre-build segmentation/ModR/M/20-bit in M3
  (`:833-841`).
- **M3.1b plan** (`docs/superpowers/plans/2026-06-13-m3b-generic-decoder.md`) — house style + the
  synthetic-CPU-driven-directly precedent (its `SyntheticDecodeStructureTests` JIT proof drives the
  generated `Decode` directly because J1 is deferred — this plan mirrors that exactly for the port +
  halt JIT proofs).

**Plan series:** M3.0 ADR ✅ · M3.1a register file ✅ · M3.1b decode walk ✅ (merged) · **M3.2: this plan
(bus I/O + interrupt/halted seams) — the LAST framework seam-generalization** · M3.3: extraction
loaders + Z80 dataset · M3.4: Z80 interpreter + TomHarte (the real `IN`/`OUT`/`HALT`/`IM` logic) · M3.5:
Z80 through JIT + J1 + the genericity findings.

---

## Derived scope — refactor vs. additive vs. confirm-only (verified against the repo, not assumed)

This milestone is unusual: most of the surface it touches **already exists and is already generic**.
The honest classification of each item, with file citations:

| Item | Classification | Why (ground truth) |
|---|---|---|
| **(1a) `Io` `AddressSpace` + `AddressSpaceKind.Io`** | **Already exists (confirm-only)** | `AddressSpaceKind.Io` is a member with a comment naming Z80/8086 (`AddressSpaceKind.cs:12`); `AddressSpace` takes a `kind` + `addressBits` and an `Io` space is constructed exactly like a `Program` one (`AddressSpace.cs:30-43`). |
| **(1b) `Machine`/`MachineBuilder` multi-bus wiring** | **Already exists (confirm-only)** | `Machine` holds a `Dictionary<AddressSpaceKind, AddressSpace>` and builds every declared space (`Machine.cs:10,36-37`); `MachineBuilder.WithAddressSpace(kind, …)` already admits `Io` (`MachineBuilder.cs:15-21`); `IMachineContext.Space(kind)` lets a CPU factory capture any space (`IMachineContext.cs:7`). A machine *can already* declare an `Io` space — `MachineBuilderTests:147` constructs one. The CPU factory just needs to *capture* it. **No `Machine`/`MachineBuilder`/`IMachineContext` change.** |
| **(1c) `PortIn`/`PortOut` micro-ops + emission** | **ADDITIVE (new code)** | No `PortInOp`/`PortOutOp` in `Op.cs`; no `PortIn`/`PortOut` in `Spec.cs`; no `IoPort*` in `AddrMode.cs`; no port class in `SpecParser`. Grep for `PortIn\|PortOut\|ReadIo\|WriteIo` finds ONLY docs/tests. This is the genuine new vocabulary. **Purely additive — the 6502 declares none.** |
| **(1d) JIT never-fastmem-the-Io-space** | **Confirm + one new emit arm** | `Fastmem` is built from ONE `AddressSpace` — `Fastmem(AddressSpace bus, …)` (`Fastmem.cs:23`), so the `Io` bus is never in the page table by construction (ADR `0001-…:218-220`). The port op needs a NEW emit arm that calls a *second* `IAddressSpace.Read8/Write8` unconditionally (no fastmem branch). **The rule is confirmed structurally; the new arm is additive.** |
| **(2) generic halted state** | **ADDITIVE (new code) + a confirmed guard** | No `HaltOp`/`Halt()`/`Halted`. The generated `Step` always fetches (`CpuEmitter.cs:99-121`). `Machine.Run`'s no-progress guard (`Machine.cs:83-85`) is the M2 carry-forward: it throws if `CycleCount` did not advance. But `ICpuCore.Step` ALREADY promises "Always advances CycleCount by at least one cycle — including … (future) halted states" (`ICpuCore.cs:18-21`) — the *contract* anticipated this. M3.2 makes a halted Step honor it (idle one cycle). **The micro-op + Step guard are new; the guard accommodation is confirming the always-advance invariant holds for halt.** |
| **(3) interrupt seam for IM 0/1/2 expressibility** | **CONFIRM-ONLY + the Io-bus reachability (from 1b/1c)** | `TryServiceInterrupt`/`InterruptPending` are per-CPU partial hooks (`Mos6502Cpu.cs:69,78`; generated declarations `CpuEmitter.cs:129` + the contract doc-comment `CpuEmitter.cs:14-19`); both tiers boundary-sample `InterruptPending` policy-blind (`CpuEmitter.cs:101`, `JittedCpu.cs:90`, `BlockCompiler.cs:526-529`); `InterruptLine` is wired-OR/multi-source (`InterruptLine.cs`, PR #11). **Nothing in Core or the generated side bakes the 6502 single-fixed-vector model** — the vector addresses live entirely in the 6502 partial (`Mos6502Cpu.cs:94`). A Z80 partial implementing IM 0/1/2 needs ONLY (a) to read a byte from the device during service — reachable via the same `Io` bus item (1) wires — and (b) its own latches (`IFF1`/`IFF2` — partial-private fields). **So M3.2's interrupt work is: confirm + document the IM-expressibility contract + a synthetic non-6502-interrupt proof. The ONLY enabling code is the Io-bus reachability item (1) delivers anyway.** |
| **(4) M4 deferred-inputs note** | **Doc-only** | A code-free record (Ground truth H). |

**Verdict — is M3.2 lighter than M3.1a/M3.1b? YES, materially.**
M3.1a *replaced* the closed `Reg` enum with a spec-declared register file (a Core type change + 3
generator-mirror rewrites + the JIT's baked-`FieldInfo` set → a per-compile name→FieldInfo map). M3.1b
*replaced* the static `OpcodeDescriptor.Length` field with a computed decode walk (a new `IDecoder`/
`DecodeResult`/`IFetchStream` family; four `switch(opcode)` decode sites collapsed to one generated
walk; a re-snapped 6502 `.g.cs`). Both were genuine refactors of load-bearing 6502-shaped seams with a
characterized generated-output delta. **M3.2 has no 6502-shaped seam to reshape:** the I/O space and
multi-bus wiring were built generic in M1; the interrupt seam was built generic in M1 3b-ii. The 6502
declares no `Io` space, no port op, and never halts, so **`Mos6502Spec.cs` is unchanged and the
generated 6502 `.g.cs` is byte-identical** (Ground truth E — no re-snap, in contrast to M3.1a/b which
both re-snapped). M3.2 is two small additive features (port micro-ops; halted state) + one confirmation
(the interrupt seam) + a doc note: a single, comfortably-sized PR. The task count (~10 TDD tasks,
~34 new tests) is roughly half M3.1b's.

---

## Derived numbers (verified against the repo, not assumed)

- **Baseline test count: ~1467** (controller-stated; confirm the EXACT number at Task 0 with a clean
  `dotnet test` and record it — the estimate below is relative to the confirmed baseline). The repo has
  **670 `[Fact]` + 39 `[Theory]` methods** (theories expand to many cases via `InlineData`, plus the
  data-driven TomHarte/Klaus sweeps, hence the ~1467 expanded figure). Per-task new-test estimate is
  tabulated under each task and summed in the self-review; **the headline estimate is ~1467 + ~34 ≈
  ~1501.**
- **Klaus cycle anchor: 96,241,367** — a PURE INVARIANT, BOTH tiers. M3.2 touches NO 6502 code path:
  no 6502 opcode, no 6502 mode, no 6502 interrupt logic. Klaus under the interpreter AND the JIT must
  reach `$3469` at EXACTLY 96,241,367 cycles, unchanged.
- **TomHarte full sweep: 1.51M cases per tier, both tiers, 0 parity failures** — unchanged. M3.2 adds
  vocabulary the 6502 never uses; any divergence is a bug by definition.
- **Generated 6502 `.g.cs` delta: NONE — byte-identical (NO re-snap).** Unlike M3.1a (`JitOp`
  index→name) and M3.1b (`Length`→`LengthRule`+walk), M3.2 adds NO field to `OpcodeDescriptor` the 6502
  emits differently, changes NO emission path the 6502 takes, and the 6502 spec declares no
  `Io`/port/halt. The new `AddrMode.IoPortImmediate`/`IoPortIndirect` + `JitMode` members are *additive*
  (no 6502 row names them → no 6502 descriptor/disassembler arm changes); the new port-class branch in
  `SpecParser`/`CpuEmitter` is only reached by a row using a port op (none in the 6502); the new `Halt`
  emission only fires for a `HaltOp` row (none in the 6502). **Derived consequence: the 6502 generator-
  snapshot test stays green WITHOUT a re-snap — itself the genericity proof point** (the additive change
  did not perturb the existing CPU). Pinned in the authorized-changes table (the snapshot does NOT move).
- **Core type-shape deltas (additive only):** `Op.cs` (+2 records), `Spec.cs` (+3 factories — `PortIn`,
  `PortOut`, `Halt`), `AddrMode.cs` (+2 members), the JIT `JitMode` mirror (+2 members) + `JitOpClass`
  (+1 `Port` member). `AddressSpaceKind` does NOT change (it already has `Io`). **`OpcodeDescriptor`'s
  record shape is UNCHANGED** — the port op rides the `JitOp.Kind` string + `JitOpClass.Port` (Ground
  truth A.3), and `Halt` rides the `Register` class + a `HaltOp` kind, so no new descriptor field — a
  second reason the 6502 `.g.cs` is byte-identical. `ICpuCore`, `IMachineContext`, `Machine`,
  `MachineBuilder`, `IAddressSpace`, `InterruptLine`, `IInterruptLine` are **all unchanged**.

---

## Ground truth A — the port-op DSL surface + the Io-targeting contract

**The port op in one sentence:** `PortIn(reg)` reads one byte from the `Io` `AddressSpace` at a port
number (the `(n)` immediate or the `(C)` register) into `reg`; `PortOut(reg)` writes `reg` to the `Io`
space at that port. Both target the **`Io` bus**, never the program/data bus, and the JIT NEVER
fastmems them — they are always a bus callout (because a port read/write is an observable device side
effect: a UART transmit, an interrupt-acknowledge — research §5).

### A.1 The `Op` records + `Spec` factories (additive — in `CpuEmulator.Core.Specification`)

```csharp
// Op.cs — additive (the I/O class)
public sealed record PortInOp(string Target) : Op;   // IN reg,(port) — read Io bus into reg
public sealed record PortOutOp(string Source) : Op;  // OUT (port),reg — write reg to Io bus

// Halt (Ground truth B — listed here for the full additive op set)
public sealed record HaltOp : Op;
```

```csharp
// Spec.cs — additive factories. Register args are register-NAME string literals (the J2 convention,
// validated against the spec's Registers table by CPUGEN008), NOT a Reg enum.
public static Op PortIn(string target) => new PortInOp(target);   // Z80: IN A,(n) / IN r,(C)
public static Op PortOut(string source) => new PortOutOp(source); // Z80: OUT (n),A / OUT (C),r
public static Op Halt() => new HaltOp();                          // Z80 HALT / 68000 STOP
```

> **Why a register NAME, not a `Reg`.** Identical to every M3.1a-era op: the Z80's `IN A,(n)` names
> `A`, `IN r,(C)` names any 8-bit register, and there is no closed register enum. The generator
> cross-checks the name against the spec's `Registers` table (CPUGEN008). The synthetic port CPU names
> a register it declares; a name the spec does not declare is a generator diagnostic.

### A.2 The addressing modes (additive — in `AddrMode.cs` + the two mirrors)

```csharp
// AddrMode.cs — additive members (the I/O port forms)
public enum AddrMode
{
    Implied, Accumulator, Immediate,
    ZeroPage, ZeroPageX, ZeroPageY,
    Absolute, AbsoluteX, AbsoluteY,
    IndirectX, IndirectY, Indirect, Relative,
    IoPortImmediate,   // (n)  — an 8-bit port-number operand byte (Z80 IN A,(n) / OUT (n),A)
    IoPortIndirect,    // (C)  — the port number comes from a register (Z80 IN r,(C) / OUT (C),r)
}
```

The two MIRROR-TABLE edits this forces (the M3.1b §11 "mode mirror tax" — paid deliberately, not
data-driven, because mode-enum data-driving is out of M3 scope):

| Mirror | File:line | Edit |
|---|---|---|
| `s_addrModes` | `SpecParser.cs:77-83` | add `"IoPortImmediate", "IoPortIndirect"` |
| `JitMode` | `OpcodeDescriptor.cs:19-25` | add `IoPortImmediate, IoPortIndirect` (the JIT data-layer copy of `AddrMode`) |

> **Why a mode at all (vs. reusing `Immediate`/`Implied`)?** Because the descriptor's mode drives BOTH
> the decode walk's operand length (`(n)` consumes one operand byte → length 2; `(C)` consumes none →
> length 1 — the FixedLength the M3.1b walk reads) AND the JIT/interpreter operand-resolution arm
> (where the port number comes from). A distinct mode keeps the port op's length + operand source
> first-class, consistent with how every other mode carries its operand shape. The 6502 has neither
> mode; they are additive.

### A.3 The class/mode matrix grows a `Port` class (additive — in `SpecParser` + `CpuEmitter` + the JIT)

The generator's class/mode matrix (`SpecParser` `ClassifyOps`/`ValidateModeForClass`; the per-class
mode sets `s_loadAluModes`/`s_storeModes`/`s_rmwModes` `SpecParser.cs:128-143`; `InstructionClass`
`SpecModel.cs`; `ClassifyForJit` `CpuEmitter.cs:1479`) grows a **Port class** admitting EXACTLY the two
I/O modes. Concretely:

```csharp
// SpecParser.cs — additive: the I/O op kinds + their legal modes.
private static readonly HashSet<string> s_portOpKinds = new(System.StringComparer.Ordinal)
{
    "PortIn", "PortOut",
};

private static readonly HashSet<string> s_portModes = new(System.StringComparer.Ordinal)
{
    "IoPortImmediate", "IoPortIndirect",
};
```

`ClassifyOps` maps a row whose first op is `PortIn`/`PortOut` to `InstructionClass.Port` (a new member);
`ValidateModeForClass` requires a `Port`-class row to use a `s_portModes` mode (CPUGEN: a port op in,
say, `Absolute` mode is rejected — the same per-class mode-legality gate every class has).
`s_microOpSignatures` gains `["PortIn"] = { ArgKind.Reg }`, `["PortOut"] = { ArgKind.Reg }`,
`["Halt"] = empty`.

> **Why the port op needs NO new `OpcodeDescriptor` field.** The JIT data layer already carries the op
> as a `JitOp(Kind, RegA, RegB, FlagBit, BoolArg)` (`OpcodeDescriptor.cs:37`); `PortIn`/`PortOut`
> serialize as `JitOp("PortIn", "A", "", 0, false)` — the register name in `RegA`, exactly like
> `Load`/`Store`. The new `JitOpClass.Port` (the JIT-side mirror of `InstructionClass.Port`) selects the
> emit arm. **No descriptor record-shape change** → the 6502 descriptors are byte-identical (Ground
> truth E). `Halt` rides the existing `Register` class with a `HaltOp` kind (Ground truth B), so it too
> adds no descriptor field.

### A.4 The CPU↔Io-bus wiring contract (the generated side + the hand-written partial)

The Io-targeting contract has three layers, mirroring how the memory bus is wired today:

1. **The generated `Step`/emission requires the partial to provide `ReadIo`/`WriteIo`** — the I/O
   analogues of the existing `ReadBus`/`WriteBus` (`Mos6502Cpu.cs:111-121`). The generator's header
   doc-comment contract (`CpuEmitter.cs:14-19`) gains: *"a CPU that declares a `Port`-class instruction
   MUST provide `byte ReadIo(uint port)` and `void WriteIo(uint port, byte value)` (which charge the
   I/O cycle timing)."* The generated port body calls them. **A CPU with no port op (the 6502) is
   never required to provide them** — so the 6502 partial is unchanged. The generator emits the
   `ReadIo`/`WriteIo` *requirement* only when the model contains a `Port`-class row (a conditional
   emission, like the existing status/SP-register-conditional emission).

2. **The hand-written partial captures the `Io` space + implements `ReadIo`/`WriteIo`.** The Z80
   partial (M3.4) will take the `Io` `AddressSpace` from the machine context and route `ReadIo`/
   `WriteIo` to it. The synthetic port CPU (Ground truth F.1) does exactly this in miniature:

   ```csharp
   // The synthetic port CPU's hand-written partial (the M3.4 Z80 wiring in miniature):
   private readonly IAddressSpace _bus;     // program/data bus (existing)
   private readonly IAddressSpace _ioBus;   // the Io AddressSpace — captured from the machine context
   public PortTestCpu(IAddressSpace bus, IAddressSpace ioBus) { _bus = bus; _ioBus = ioBus; }
   private byte ReadIo(uint port) { _cycles++; return _ioBus.Read8(port); }
   private void WriteIo(uint port, byte value) { _cycles++; _ioBus.Write8(port, value); }
   ```

   The machine wiring is the **already-generic** path (Derived scope 1b): the machine declares an `Io`
   space and the CPU factory captures it:

   ```csharp
   var machine = Machine.Create("porttest")
       .WithAddressSpace(AddressSpaceKind.Program, 16)
       .WithAddressSpace(AddressSpaceKind.Io, 16)        // ALREADY supported (MachineBuilder.cs:15)
       .WithCpu(ctx => new PortTestCpu(
           ctx.Space(AddressSpaceKind.Program),
           ctx.Space(AddressSpaceKind.Io)))              // capture the Io space (IMachineContext.cs:7)
       .Build();
   ```

3. **The JIT routes the port-op callout to a SECOND `IAddressSpace`.** `JittedCpu` today holds one
   `_calloutBus` (`JittedCpu.cs:21,61`); for a CPU with an `Io` space the JIT also holds an
   `_ioCalloutBus`, and the port emit arm calls `IAddressSpace.Read8/Write8` on it **unconditionally**
   — no fastmem branch (Ground truth D). The Z80's actual JIT wiring is M3.5 (J1 makes the compiler
   generic over the CPU type); the synthetic port CPU's JIT proof (Ground truth F.1) drives the
   generated emit primitive directly (the M3.1b precedent), not the live `BlockCompiler`.

> **The Io-targeting INVARIANT (the load-bearing contract):** a `PortIn`/`PortOut` micro-op's
> interpreter body calls `ReadIo`/`WriteIo` (the `Io` bus), and its JIT arm calls the
> `Io`-bus `IAddressSpace` callout — **NEVER** `ReadBus`/`WriteBus`/`LoadByteFromBus`/`EmitStoreByte`
> (the memory bus + its fastmem branch). The synthetic port CPU asserts this directly: a `PortIn` reads
> the byte the test placed on the `Io` space at the port number, and the SAME address on the program
> space holds a DIFFERENT byte — so a body that hit the wrong bus reads the wrong value and the test
> fails. This is the "port `0x00` is not memory `0x00`" property (ADR `0001-…:205-206`).

---

## Ground truth B — the generic halted-state contract

**The halted state in one sentence:** a `Halt()` micro-op sets a CPU-internal `_halted` latch; while
`_halted` is set, `Step` idles exactly one cycle (advancing `CycleCount`, NOT fetching/executing) until
an interrupt is serviced, which clears the latch — so the CPU "executes NOPs until an interrupt" (the
Z80 `HALT` / 68000 `STOP` behavior) WITHOUT busy-spinning and WITHOUT tripping any no-progress guard.

### B.1 The pieces (all additive; the latch lives in the hand-written partial)

1. **`HaltOp` + `Spec.Halt()`** (Ground truth A.1) — the micro-op a `HALT`/`STOP` row carries.
   `Halt` is a `Register`-class (Implied-mode) op: it does the implied dummy-read shape then sets the
   latch. It needs no operand and no new class — `s_registerOpKinds` gains `"Halt"`, and the
   `EmitRegisterBody`/`EmitRegister` arms gain a `Halt` case.

2. **The `_halted` latch + a `partial bool Halted` hook** — the latch is a partial-private field (like
   the 6502's `_nmiPending`); the generated `Step` reads it through a `partial bool Halted` hook (the
   same partial-hook altitude as `InterruptPending`, `CpuEmitter.cs:129`). The generated header
   doc-comment contract gains the requirement: *"a CPU that uses `Halt()` MUST provide
   `public partial bool Halted` and a `Halt()` micro-op body that sets the halted latch; the latch
   MUST clear when an interrupt is serviced (in `TryServiceInterrupt`)."* A CPU with no `Halt()` op
   (the 6502) provides neither — the generator emits the `Halted`-hook consultation in `Step` ONLY when
   the model contains a `HaltOp` (a conditional emission, so the 6502 `Step` is byte-identical).

3. **The generated `Step` halted guard** — when the model contains a `HaltOp`, `Step` becomes:

   ```csharp
   public void Step()
   {
       if (TryServiceInterrupt())   // services a pending interrupt AND clears _halted (the wake)
           return;
       if (Halted)                  // halted: idle exactly one cycle, do NOT fetch/execute
       {
           IdleCycle();             // _cycles++ (the partial provides this; it is the "NOP while halted")
           return;
       }
       // ... the normal fetch/decode/execute (UNCHANGED for a CPU with no HaltOp) ...
   }
   ```

   `IdleCycle()` is a partial helper (`_cycles++`) the halt-using CPU provides — it is the single cycle
   a halted CPU charges per `Step`, satisfying the `ICpuCore.Step` always-advance contract
   (`ICpuCore.cs:18-21`). **The wake:** `TryServiceInterrupt` runs FIRST, so when an interrupt is
   pending the partial services it (the partial clears `_halted` as part of servicing — the Z80 clears
   `HALT` when `INT`/NMI is taken) and `Step` returns having advanced. On the next `Step` `Halted` is
   false and normal fetch resumes.

### B.2 The `Machine.Run` no-progress guard — confirmed, not changed

`Machine.Run` throws if a `Cpu.Run` slice did not advance `CycleCount` (`Machine.cs:83-85`) — the M2
carry-forward concern: a halted CPU that did NOT advance would trip it. **The resolution is the
always-advance invariant, not a guard change.** Because a halted `Step` charges one idle cycle (B.1),
`Cpu.Run` (the generated `while (cycleBudget > 0) { Step(); budget -= delta; }` loop,
`CpuEmitter.cs:132-140`) always advances ≥1 cycle per `Step`, so `Machine.Run`'s
`if (Cpu.CycleCount <= before) throw` NEVER fires for a halted-but-idling CPU. **No change to
`Machine.Run`.** The synthetic halt CPU (Ground truth F.2) pins this: a machine running a halted CPU
for a cycle budget returns the budget consumed (idle cycles), does NOT throw, and the CPU wakes when an
interrupt is asserted mid-run.

> **Why a guard CHANGE is the wrong fix.** The brief and the `StuckCpu` test double
> (`tests/.../TestDoubles/StuckCpu.cs`) exist precisely so the guard CATCHES a genuinely stuck CPU (an
> infinite no-progress loop). Relaxing the guard to "allow no progress" would defeat its purpose. The
> right fix is the one the `ICpuCore.Step` contract already names: a halted CPU *advances cycles*
> (idle), so it is not "stuck" — it is making the legitimate progress of a halted processor (the bus
> still clocks; the refresh counter still counts on a real Z80). **The halted state is progress, not
> stall** — that is the conceptual key, and it is why the guard is confirmed correct as-is.

### B.3 The JIT dispatcher halted fast path

`JittedCpu.Run` (`JittedCpu.cs:86-104`) loops `GetOrCompile((ushort)PC)` + `RunChain`. A block of
`HALT` would otherwise compile a one-instruction block that sets `_halted` and exits, then the
dispatcher would re-enter, re-check `InterruptPending`, and `GetOrCompile` the SAME block again — a
correct but wasteful spin (compiling/dispatching once per idle cycle). The fast path: the dispatcher
checks `_inner.Halted` (the same hook) at the top of the loop, and while halted-and-no-interrupt it
charges the idle cycles in a tight loop WITHOUT compiling/dispatching a block:

```csharp
public void Run(ref long cycleBudget)
{
    while (cycleBudget > 0)
    {
        if (_inner.InterruptPending) { /* service via inner.Step (UNCHANGED) */ continue; }
        if (_inner.Halted)            // halted fast path: idle to the next interrupt/budget edge
        {
            long before = _inner.CycleCount;
            _inner.Step();            // one idle cycle (Step's halted guard, B.1)
            cycleBudget -= _inner.CycleCount - before;
            continue;                 // re-check InterruptPending next iteration (the wake)
        }
        // ... InvalidateIfDirty + GetOrCompile + RunChain (UNCHANGED) ...
    }
}
```

> **Why route the halted idle through `_inner.Step` (not an emitted halt block).** A halted CPU does no
> memory access, sets no flags, follows no chain — there is nothing to emit. Delegating the idle to
> `_inner.Step` (the interpreter) keeps the halted path in ONE place (the interpreter's `Step` halted
> guard, B.1) and avoids compiling a degenerate block. This mirrors the existing `InterruptPending`
> branch, which already delegates servicing to `_inner.Step` (`JittedCpu.cs:90-95`). The halted fast
> path is the EXACT same shape, one branch above. The synthetic halt CPU drives this only at the
> interpreter tier in M3.2 (J1 deferred); the JIT-dispatcher branch is added + unit-pinned via a small
> direct test (Ground truth F.2 note), with the live-JIT halted run a recorded M3.5 follow-up.

> **68000 `STOP` reuse (the forward design constraint, NOT built):** `STOP` loads `SR` then halts until
> an interrupt of sufficient level (68000 brief §6.3, `:485,553-555`). The *halted* half is IDENTICAL
> to the Z80 `HALT` — the same `_halted` latch + `Step` idle guard + dispatcher fast path. The only
> 68000-specific part (the `SR`-level gate on the wake) lives in the 68000 partial's
> `TryServiceInterrupt` (M4), not in the generic halted mechanism. **M3.2 builds the halted state so
> that reuse is verbatim; it does not build `STOP`.** Recorded (Ground truth H).

---

## Ground truth C — the interrupt-seam contract (what is generic-already vs. newly-generalized)

**The honest finding, stated plainly: the interrupt seam is ALREADY generic. M3.2 adds ZERO new
interrupt machinery. The only enabling change is the Io-bus reachability that item (1) delivers
anyway.** This section is the requested honest assessment of how much is already generic, the
IM-expressibility contract it must satisfy, and the one synthetic proof.

### C.1 What is ALREADY generic (confirm-only — citations)

| Seam | Already generic? | Evidence |
|---|---|---|
| **Interrupt SERVICING is per-CPU hand-written** | YES | `Mos6502Cpu.TryServiceInterrupt` (`Mos6502Cpu.cs:78-100`) does the 6502's fixed-vector 7-cycle sequence. The vector addresses (`$FFFA`/`$FFFE`) appear ONLY in the partial (`:94`). The generated side declares `private partial bool TryServiceInterrupt()` (`CpuEmitter.cs:129`) and calls it — it does not know the policy. |
| **The pending PREDICATE is per-CPU** | YES | `InterruptPending` is a `public partial bool` (`Mos6502Cpu.cs:69`) — `_nmiPending \|\| (_irqLine && I-clear)` is 6502-specific and lives in the partial. The generated side only consults the hook. |
| **Both tiers boundary-SAMPLE the predicate, policy-blind** | YES | Interpreter `Step` calls `TryServiceInterrupt()` before the fetch (`CpuEmitter.cs:101`); JIT dispatcher checks `_inner.InterruptPending` at block entry (`JittedCpu.cs:90`) and the chain edge samples it (`BlockCompiler.cs:526-529`). Neither knows what "pending" means. |
| **The interrupt LINE is wired-OR, multi-source** | YES | `InterruptLine`/`IInterruptLine` (`InterruptLine.cs`, PR #11) compute a wired-OR level across per-device handles. The Z80's single `INT` line + NMI reuse this as-is. |
| **The line BINDING is late-bound + level-replaying** | YES | `Machine.LateBoundLine` (`Machine.cs:101-118`) binds the line to the CPU after construction, replaying the level. CPU-agnostic. |

**Nothing in `Core` or the generated layer bakes the 6502's single-fixed-vector model.** The 6502's
"two-byte vector at `$FFFE`" is entirely a fact of `Mos6502Cpu.TryServiceInterrupt`. This is exactly
ADR Decision 5's positive finding (`0001-…:434-438`): *"the interrupt seam is the one place the
framework is already generic … M3 confirms it."* And the 8086 brief confirms the same seam expresses
its third interrupt shape (the IVT) "exactly as the 6502 does — no Core change" (`8086-…:568`).

### C.2 The IM-expressibility contract (what a Z80 partial MUST be able to do — and CAN, today)

For a Z80 hand-written partial (M3.4) to implement `IM 0/1/2` + NMI + `IFF1`/`IFF2`, the seam must let
the partial:

1. **Decide "pending" with its own latches.** ✅ Already possible: `InterruptPending` is the partial's
   own `bool` expression. The Z80's `(_intLine && IFF1) || _nmiPending` is a partial-private predicate
   over partial-private `IFF1`/`IFF2`/`_intLine`/`_nmiPending` fields — the identical shape as the
   6502's, no Core change.

2. **Perform an arbitrary service sequence with its own vectoring.** ✅ Already possible:
   `TryServiceInterrupt` is the partial's method. `IM 1` (fixed `RST 38h` → `0x0038`) and NMI (fixed
   `0x0066`) are constant-vector services — structurally identical to the 6502's, just different
   constants. `IM 2` (vectored: `I`-register high byte + a device-supplied low byte → a pointer into a
   table → the handler address) is a *table* lookup the partial does with its own `I` field + a bus
   read. The vectoring math is partial-internal. **No Core/generated change.**

3. **Read a byte FROM THE DEVICE during service** (the ONLY new requirement). `IM 0` (the device puts
   an opcode — usually `RST n` — on the data bus) and `IM 2` (the device supplies the vector low byte)
   need an **interrupt-acknowledge bus cycle**: the partial reads a byte the device places on the bus.
   On the Z80 this is an I/O-space-flavored acknowledge cycle. ✅ **Reachable via the `Io` bus item (1)
   wires** — the partial holds the `Io` `AddressSpace` and reads the acknowledge byte through it (or a
   dedicated acknowledge hook the device implements as an `IPeripheral` on the `Io` space). This is the
   one piece that was NOT reachable before M3.2 (the partial had no second bus); item (1) makes it
   reachable. ADR `0001-…:427-429,436`: *"the interrupt-acknowledge read (for IM 0/IM 2) routes through
   the I/O bus wiring from Decision 2."*

4. **Track an `EI`-delay latch.** ✅ Already possible: a partial-private "just enabled" bool, set on
   `EI` and consumed one instruction later — the same shape as the 6502's documented CLI/SEI-delay
   deviation (a partial concern, not a Core one). M3.4 implements it; M3.2 confirms the seam allows it.

**Conclusion: the interrupt seam needs NO generalization for IM 0/1/2 to be expressible — it is already
sufficient. The single enabling change (the Io-bus reachability) is delivered by item (1). M3.2's
interrupt deliverable is therefore: (a) this documented confirmation, and (b) a synthetic non-6502-
interrupt-model test proving the seam expresses a non-fixed-vector shape (Ground truth F.2).**

### C.3 The newly-DOCUMENTED contract (the deliverable — no code)

The generated header doc-comment contract (`CpuEmitter.cs:14-19`) is extended with the IM-expressibility
note so the M3.4 author has the contract in front of them:

> *A CPU's `TryServiceInterrupt` MAY implement any boundary-sampled interrupt policy: a fixed vector
> (6502), a mode-selected vector (Z80 IM 0/1/2: device-supplied opcode / fixed `RST 38h` / I-register +
> device-supplied table index), an NMI with its own vector, or a vector table (8086 IVT). It performs
> the full service bus sequence itself (charging cycles via `ReadBus`/`WriteBus`, and — for a device-
> supplied vector byte — `ReadIo` for the interrupt-acknowledge cycle), clears any halted latch (the
> `HALT`/`STOP` wake), and returns true. The generated side is policy-blind; it only calls the hook and,
> if it returns false, proceeds to the (halt-or-)fetch path.*

> **The genericity proof point (positive finding):** that M3.2 changes NO interrupt machinery — only
> *documents* the contract and *enables* the Io-bus acknowledge read — IS the proof the M1 interrupt-
> seam design was genuinely CPU-agnostic, not 6502-shaped. Per ADR `0001-…:440-444`: *"if anything here
> needs a Core change, that is a finding; if nothing does, that is the proof point."* **Nothing here
> needs a Core change. That is the finding.**

---

## Ground truth D — the JIT never-fastmems the Io space (confirmed structurally + the new port arm)

**The rule:** a port op's JIT arm calls the `Io`-bus `IAddressSpace.Read8/Write8` UNCONDITIONALLY — it
never takes the fastmem direct-array branch. This is true by TWO independent guarantees:

1. **Structural (the Io bus is never in the fastmem page table).** `Fastmem` is constructed from ONE
   `AddressSpace` — the memory bus (`Fastmem.cs:23`, `JittedCpu.cs:62`). The `Io` `AddressSpace` is a
   DIFFERENT object the `Fastmem` ctor never sees, so its pages are never in `PageBacking[]`. Even if
   the port arm *mistakenly* called `LoadByteFromBus`, that helper indexes the MEMORY fastmem table by
   the port number as if it were a memory address — a bug the synthetic test would catch (it would read
   the wrong bus). The correct arm does not use `LoadByteFromBus` at all.

2. **By construction (the new port emit arm).** The port arm is a NEW, minimal emit method — the same
   shape as the MMIO arm of `EmitStoreByte`/`LoadByteFromBus` (the bus-callout branch,
   `BlockCompiler.cs:338-343, 414-420`), but to the `Io`-bus `IAddressSpace` and with NO fastmem branch
   at all:

   ```csharp
   // BlockCompiler.Emit.cs — additive: the Port-class emit arm. NO fastmem branch — always a callout.
   private void EmitPort(EmitContext ctx, OpcodeDescriptor d)
   {
       string reg = d.Ops[0].RegA;                 // the register named by PortIn/PortOut
       EmitPortNumber(ctx, d);                     // push the port number (uint): (n) operand or (C) reg
       if (d.Ops[0].Kind == "PortIn")
       {
           EmitChargeOneCycle(ctx);                // I/O cycle (the partial's ReadIo charges one too)
           ctx.Il.Emit(OpCodes.Ldarg, ArgIoBus);  // the SECOND IAddressSpace (never the fastmem'd bus)
           // stack: portNumber, ioBus -> reorder so it's ioBus.Read8(portNumber)
           // (the literal stack-shuffle is Task 5's job; shape: ioBus.Read8(port) -> store into reg)
           EmitIoRead(ctx);                        // callvirt IAddressSpace.Read8 on the Io bus
           ctx.Il.Emit(OpCodes.Conv_U1);
           ctx.Il.Emit(OpCodes.Stfld, RegField(reg));
       }
       else // PortOut
       {
           EmitChargeOneCycle(ctx);
           EmitLoadRegOrA(ctx, reg);               // push reg value
           EmitIoWrite(ctx);                       // callvirt IAddressSpace.Write8 on the Io bus
       }
   }
   ```

   `ArgIoBus` is a NEW `BlockDelegate` parameter (the `Io`-bus `IAddressSpace`) the JIT passes when a
   compiled block contains a port op. **For the 6502, no block contains a port op, so the parameter is
   unused** (or, since the 6502 has no `Io` space, the JIT for the 6502 passes `null`/a no-op — the
   parameter is additive to the delegate signature; the 6502's emitted blocks never reference it, so
   their IL is unaffected). The live-JIT wiring of `ArgIoBus` is M3.5 (J1); M3.2's synthetic proof
   drives `EmitPort` / the generated interpreter port body directly (Ground truth F.1).

> **Why the port arm is EMITTED, not a fallback (recorded deviation, restated here in context).** The
> arm above is ~8 IL ops — trivially small, and the WHOLE POINT of the milestone is to prove the
> never-fastmem rule in emitted IL. Falling back would route the port op through `_inner.Step`, which
> would prove the rule for the INTERPRETER but not the JIT. Emitting it proves both tiers hit the `Io`
> bus. (Contrast: ADR Decision 4 lists block-ops/`DAA`/`EX (SP),HL` as fallback candidates — those are
> genuinely complex; the port op is not.)

> **The 16-bit-memory-op note (out of scope, recorded):** the Z80's `LD HL,(nn)` does TWO byte accesses
> to the MEMORY bus (composable from the existing byte fastmem helpers — ADR J4, `0001-…:508`). That is
> M3.4/M3.5 and uses the existing memory fastmem, NOT the port arm. The port arm is exclusively for the
> `Io` space. No 16-bit/word bus work here (Ground truth H).

---

## Ground truth E — the generated-6502-output delta: NONE (byte-identical, no re-snap)

**The honest statement: the 6502's generated `Mos6502Cpu.g.cs` is BYTE-IDENTICAL after M3.2. There is
NO re-snap.** This is the cleanest possible outcome and is itself the genericity proof: an additive
seam that does not perturb the existing CPU.

| Generated/committed artifact | Changes? | Why |
|---|---|---|
| `Mos6502Cpu.g.cs` — `Step`/`Run`/`Execute` | **NO** | the halted guard is emitted ONLY when the model has a `HaltOp` (conditional emission); the 6502 has none, so `Step` is byte-identical. `Run`/`Execute` untouched. |
| `Mos6502Cpu.g.cs` — `Op{XX}()` bodies | **NO** | no 6502 op is a port op or a halt op; the new `Port`/`Halt` body templates are never reached. |
| `Mos6502Cpu.g.cs` — `JitDescriptors` (the dense [256] table) | **NO** | `OpcodeDescriptor`'s record shape is UNCHANGED (the port op rides `JitOp.Kind` + `JitOpClass.Port`, no new field — Ground truth A.3); every 6502 row's literal is identical. The new `JitOpClass.Port` enum member is additive (no 6502 row uses it). |
| `Mos6502Cpu.g.cs` — `Decode`/`DescriptorFor` walk (M3.1b) | **NO** | the 6502 declares no `DecodeStructure`, takes the degenerate byte-key walk; the new `IoPort*` modes are additive enum members no 6502 row names. |
| `Mos6502Cpu.g.cs` — `Disassemble`/`InstructionLength`/`TryAssemble` | **NO** | no new disassembler arm fires (no `IoPort*` 6502 row); the new mode's disasm format (`IN`/`OUT` text) is reached only by a port-op row (none in the 6502). |
| `Mos6502Cpu.g.cs` — `ReadIo`/`WriteIo`/`IdleCycle` requirement | **NO** | the generator emits the `ReadIo`/`WriteIo` + `Halted`/`IdleCycle` *requirement* ONLY when the model has a `Port`/`Halt` op; the 6502 partial provides none and is not required to. |
| `Mos6502Spec.cs` (importer output) | **NO** | the 6502 spec is unchanged — no `Io` space, no port op, no halt. `RegeneratedSpecTests` byte-equality anchor holds unchanged. |
| `OpcodeDescriptor.cs` (Core type) | **NO record-shape change** | the `JitOpClass` enum gains a `Port` member + `JitMode` gains two members (additive enum values); the `OpcodeDescriptor` RECORD is unchanged → no positional-arg churn in any descriptor literal. |
| Klaus cycle count / TomHarte case results, BOTH tiers | **NO** (pure invariant) | M3.2 touches no 6502 code path. |

**Why byte-identical (and why this differs from M3.1a/M3.1b).** M3.1a re-snapped because the `JitOp`
serialization changed shape (index→name) for EVERY op — including the 6502's. M3.1b re-snapped because
`OpcodeDescriptor.Length` (a positional arg on every descriptor literal) became `LengthRule,
FixedLength` — changing every 6502 row's text, AND added the `Decode`/`DescriptorFor` methods to the
6502 `.g.cs`. **M3.2 adds NO field to any per-row serialized type and emits NO new method into a
no-port/no-halt CPU's `.g.cs`.** Every new member is reached only by a row the 6502 does not have. The
additive enum members (`JitOpClass.Port`, `JitMode.IoPort*`) are *type-level* additions that do not
appear in any 6502-emitted literal. **Derived consequence: the 6502 generator-snapshot test passes
WITHOUT a re-snap.** The authorized-changes table (Ground truth G) lists the snapshot as a test that
does NOT move — and a snapshot diff appearing for the 6502 is a STOP (it would mean an additive change
leaked into the 6502 path, i.e. a bug).

> **The pinning strategy.** Because there is no characterized delta to capture, the proof is the
> ABSENCE of a diff: Task 0 records the current 6502 `.g.cs` hash; the final task asserts it is
> unchanged (or simply that the unchanged generator-snapshot + `RegeneratedSpecTests` + TomHarte + Klaus
> all stay green). This is a stronger statement than M3.1a/b could make and is the headline genericity
> result of M3.2.

---

## Ground truth F — the two synthetic test CPUs

Both synthetic CPUs follow the M3.1a/M3.1b precedent (`SyntheticRegisterSetTests`,
`SyntheticDecodeStructureTests`): a GENERATOR/JIT fixture compiled via `GeneratorTestHost.CompileAndLoadType`,
NOT a shipped CPU, NOT the Z80/8086. The JIT-reachable proofs drive the generated emit/decode
primitives DIRECTLY (J1 deferred to M3.5 — `typeof(Mos6502Cpu)` is still baked in `BlockCompiler`), with
the live-`BlockCompiler` second-CPU run a recorded M3.5 follow-up — the EXACT posture
`SyntheticDecodeStructureTests.Discover_advances_by_the_computed_length_over_MODRMOP` documents.

### F.1 The port CPU — `SyntheticPortIoTests` (item 1 proof)

A CPU declaring an `Io` space (via the machine) + a `PortIn`/`PortOut` op, exercising both tiers hitting
the `Io` bus, not the program bus, and confirming fastmem never serves the `Io` space.

```csharp
[CpuSpecification("porttest")]
public static class PortTestSpec
{
    public static readonly RegisterDef[] Registers =
    [
        new("A", 8),
        new("PC", 16, RegisterRole.ProgramCounter),
    ];

    public static readonly InstructionDef[] Instructions =
    [
        // IN A,(n): read the Io bus at the (n) port operand into A. Length 2 (opcode + port byte).
        Insn(0xDB, "IN", AddrMode.IoPortImmediate, [PortIn("A")]),
        // OUT (n),A: write A to the Io bus at the (n) port operand. Length 2.
        Insn(0xD3, "OUT", AddrMode.IoPortImmediate, [PortOut("A")]),
        Insn(0xEA, "NOP", AddrMode.Implied, []),   // a benign terminator
    ];
}

// The hand-written partial: captures BOTH buses; ReadIo/WriteIo route to the Io space (A.4).
public sealed partial class PortTestCpu
{
    private readonly IAddressSpace _bus;
    private readonly IAddressSpace _ioBus;
    public PortTestCpu(IAddressSpace bus, IAddressSpace ioBus) { _bus = bus; _ioBus = ioBus; }
    public void Reset() { }
    public void SetIrqLine(bool a) { }
    public void SetNmiLine(bool a) { }
    private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
    private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
    private byte ReadIo(uint port) { _cycles++; return _ioBus.Read8(port); }       // the Io targeting
    private void WriteIo(uint port, byte v) { _cycles++; _ioBus.Write8(port, v); } // the Io targeting
    private void HandleUndefinedOpcode(byte op) { _cycles++; }
    private partial bool TryServiceInterrupt() => false;
    public partial bool InterruptPending => false;
}
```

The assertions:

| Test | What it proves |
|---|---|
| `Spec_generates_a_compiling_class_with_a_Port_arm` | the `Port` class + `IoPort*` modes generate clean; `result.GeneratorDiagnostics.IsEmpty` (M3.1a precedent posture). |
| `IN_reads_the_Io_bus_not_the_program_bus` (interpreter) | place `0x42` at `Io[0x10]` and a DIFFERENT byte `0x99` at `Program[0x10]`; run `IN A,(0x10)`; assert `A == 0x42`. A body that hit the program bus reads `0x99` → fails. |
| `OUT_writes_the_Io_bus_not_the_program_bus` (interpreter) | run `OUT (0x10),A` with `A=0x42`; assert `Io[0x10] == 0x42` AND `Program[0x10]` unchanged. |
| `Port_op_charges_the_Io_cycle` | `CycleCount` advances by the I/O instruction's cycles (the `ReadIo`/`WriteIo` `_cycles++` + the operand/opcode fetches). |
| `Fastmem_never_serves_the_Io_space` | construct `Fastmem` over the Program bus; assert NO page maps the Io space (the Io `AddressSpace` is a different object — structural, Ground truth D). Direct unit assertion on `Fastmem.PageBacking`. |
| `EmitPort_arm_hits_the_Io_bus` (JIT, direct-emit) | drive the generated `EmitPort` primitive / a minimal `DynamicMethod` over the port arm with a stub Io `IAddressSpace`; assert the callout lands on the Io bus, never `LoadByteFromBus`. (The direct-emit posture; live-`BlockCompiler` is M3.5.) |

### F.2 The halt + non-6502-interrupt CPU — `SyntheticHaltInterruptTests` (items 2+3 proof)

A CPU declaring a `Halt()` op and a hand-written partial whose `TryServiceInterrupt` implements a
*vectored-from-a-table* service (NOT the 6502 fixed vector) — proving the seam expresses a non-6502
interrupt SHAPE, that a halted CPU idles + does not trip the no-progress guard, and that it wakes on the
serviced interrupt.

```csharp
[CpuSpecification("haltirqtest")]
public static class HaltIrqTestSpec
{
    public static readonly RegisterDef[] Registers =
    [
        new("A", 8),
        new("PC", 16, RegisterRole.ProgramCounter),
    ];

    public static readonly InstructionDef[] Instructions =
    [
        Insn(0x76, "HALT", AddrMode.Implied, [Halt()]),   // the generic halted state
        Insn(0xEA, "NOP", AddrMode.Implied, []),
    ];
}

public sealed partial class HaltIrqTestCpu
{
    private readonly IAddressSpace _bus;
    private bool _halted;
    private bool _intLine;
    public byte VectorBase;                              // the table base — a NON-6502 vectoring input
    public HaltIrqTestCpu(IAddressSpace bus) { _bus = bus; }
    public void Reset() { _halted = false; }
    public void SetIrqLine(bool a) => _intLine = a;
    public void SetNmiLine(bool a) { }
    private byte ReadBus(uint addr) { _cycles++; return _bus.Read8(addr); }
    private void WriteBus(uint addr, byte v) { _cycles++; _bus.Write8(addr, v); }
    private void HandleUndefinedOpcode(byte op) { _cycles++; }
    private void IdleCycle() { _cycles++; }              // the "NOP while halted" (Ground truth B.1)
    public partial bool Halted => _halted;               // the Step halted hook
    // The Halt() micro-op body sets the latch — the generated Register-arm Halt case calls this:
    private void DoHalt() { _halted = true; }

    public partial bool InterruptPending => _intLine;    // partial-private predicate (non-6502 shape)
    // A NON-6502 interrupt service: vector through a TABLE the partial reads from the bus, indexed by
    // VectorBase — proving the seam is not fixed-vector-shaped. Clears _halted (the wake).
    private partial bool TryServiceInterrupt()
    {
        if (!_intLine) return false;
        _halted = false;                                 // the wake (Z80 HALT clears on INT)
        uint slot = 0xFF00u + VectorBase;                // table-indexed vectoring (NOT $FFFE)
        uint lo = ReadBus(slot), hi = ReadBus(slot + 1);
        PC = (ushort)(lo | (hi << 8));
        return true;
    }
}
```

The assertions:

| Test | What it proves |
|---|---|
| `Halt_spec_generates_a_compiling_class` | the `Halt()` op + the `Halted`/`IdleCycle` requirement generate clean. |
| `Halted_cpu_idles_one_cycle_per_step` | after `HALT`, each `Step` advances `CycleCount` by exactly one (the idle cycle) and does NOT advance PC / fetch. |
| `Machine_Run_does_not_trip_the_no_progress_guard_on_a_halted_cpu` | a `Machine` running a halted `HaltIrqTestCpu` for a cycle budget returns the budget consumed and does NOT throw `EmulationException` (Ground truth B.2 — the guard is confirmed correct, not changed). |
| `Halted_cpu_wakes_on_a_serviced_interrupt` | assert the IRQ line mid-run; the next `Step` services it (clearing `_halted`), PC jumps to the table vector; the following `Step` resumes normal fetch. |
| `Interrupt_service_vectors_through_a_table_not_a_fixed_address` | set `VectorBase = 4`, place a handler address at `0xFF04`; service; assert PC == that address (NOT a 6502 `$FFFE` vector). **The non-6502 interrupt-shape proof.** |
| `Jit_dispatcher_halted_fast_path_idles` (direct) | a small unit pin on the `JittedCpu.Run` halted branch (Ground truth B.3): a halted inner CPU drives the idle-cycle delegation without compiling a block. (Direct/seam-level; the live-JIT halted run over a second CPU type is M3.5.) |

> **Scope honesty (restated):** the synthetic interrupt service is a TABLE lookup — the minimum that is
> demonstrably NOT the 6502's fixed vector. It is NOT the real Z80 `IM 2` (no `I` register, no
> device-supplied low byte, no `IFF1`/`IFF2`, no `EI` delay) and NOT the 8086 IVT. It proves only
> EXPRESSIBILITY: a partial can vector arbitrarily. The real IM 0/1/2 is M3.4.

---

## Ground truth G — M4 deferred inputs (recorded, NOT built)

A code-free record of the two M4 growth points this milestone deliberately does not build, so they are
planned findings rather than surprises (per 68000 brief §"M3 NOW" items 9-10):

1. **Wide-bus transaction surface stays ADDITIVE — `Read16/32`/`Write16/32` + endianness = M4.**
   `IAddressSpace` stays byte-only (`Read8`/`Write8`). M3.2 adds NO wide accessor and NO endianness
   property. The 68000 (M4) is the first true wide + big-endian bus consumer (68000 brief §2, §8); the
   decision between option A (add `Read16/32` with endianness to `IAddressSpace`) and option B (compose
   from `Read8` with a per-CPU endianness policy) is the load-bearing M4 decision (68000 M4 open-q 1,
   `:806-812`) needing owner sign-off. The Z80's 16-bit memory ops decompose into two `Read8`s (M3.4),
   so even the Z80 does not force this. **Recorded: the bus surface is byte-only and additive; wide
   transactions + endianness are planned M4 contract growth, not an unstated invariant cemented now.**

2. **The 3-bit IPL interrupt-line LEVEL = M4 interrupt-seam nudge.** `IInterruptLine` carries a `bool`
   today; the 68000 needs a 3-bit `IPL0-2` priority level gated against the `SR` 3-bit mask (68000
   brief §6.2, `:515,539`). The Z80's `INT`/NMI are boolean, so M3.2 does NOT implement a level. The
   `STOP`/`HALT` wake-on-sufficient-level lives in the 68000 partial (M4), reusing the generic halted
   state (Ground truth B.3). **Recorded: a level-carrying `IInterruptLine` (or a parallel level input)
   is the one likely interrupt-seam contract growth M4 forces — a planned finding, not a surprise.**

3. **The 8086 IVT is a third interrupt shape — expressible TODAY, built at M5.** The 8086's
   vector-table interrupt model (256 entries at physical `$00000`, `INT n`, `IF` flag) is, per the 8086
   brief (`:537-568`), implementable in a partial's `TryServiceInterrupt` "exactly as the 6502 does — no
   Core change." M3.2's interrupt-seam confirmation (Ground truth C) already covers it: the IVT is just
   another partial-internal vectoring shape. **Recorded: no M3.2 work; the seam already expresses it.**
   Likewise the 8086 **shares the I/O space** — its `IN`/`OUT` reuse exactly the `Io` micro-ops this
   milestone adds (8086 brief §10.2, "pre-paid", `:244-245`).

---

## Authorized test changes (the only existing tests that move)

This milestone is ADDITIVE + CONFIRM-ONLY: the existing suite must stay green with **at most** the
changes enumerated here. **The expectation is ZERO existing-test changes** — every deliverable is new
vocabulary the 6502 never uses. A Task 0 grep (`OpcodeDescriptor(`, `JitMode\.`, `JitOpClass\.`,
`AddrMode\.`, `s_addrModes`, `ClassifyForJit`, `WithAddressSpace`, the generator-snapshot file)
produces the exact at-risk set; the table below is the predicted set — a hit not in this list is a STOP.

| # | Test (file) | Change | Why authorized (or why it should NOT move) |
|---|---|---|---|
| 1 | `OpcodeDescriptorTests` (`tests/.../Jit/OpcodeDescriptorTests.cs`) | **Likely NONE.** If any test enumerates `JitMode`/`JitOpClass` members exhaustively (e.g. "all modes have a disasm format"), it gains the two new modes + the `Port` class. | the enums gained additive members; an exhaustive-enumeration test must include them. A test constructing an `OpcodeDescriptor` does NOT move (record shape unchanged — Ground truth A.3/E). |
| 2 | The generator-snapshot test for the 6502 (`tests/.../Generators/…snapshot…`) | **MUST NOT MOVE — no re-snap.** | the 6502 `.g.cs` is byte-identical (Ground truth E). A diff here is a STOP (an additive change leaked into the 6502 path). This is the headline genericity pin. |
| 3 | `MachineBuilderTests` (`:147` constructs an `Io` space lookup) | **Likely NONE.** A NEW test may be added (a machine WITH an `Io` space resolves it) but the existing "undeclared `Io` throws" test is unchanged. | the existing test asserts an UNdeclared `Io` throws; the new port CPU declares one — orthogonal. |
| 4 | `ModeOpValidationTests` / `InstructionParsingTests` (`tests/.../Generators/`) | **Likely NONE; possibly +N new cases** for the `Port` class/mode legality (a port op in a non-port mode is rejected). | the class/mode matrix gained a `Port` class — new positive/negative validation cases are ADDED, not changed; existing 6502-mode cases are untouched. |
| 5 | `SemanticsMap`/`SpecFileEmitter`/`OpcodeDataset` importer tests | **NONE in M3.2.** | the importer-loader extension (the `IoPort*` modes + `PortIn`/`PortOut`/`Halt` factories in `OpcodeDataset.ValidModes`/`SemanticsMap.FactoryArity`/`SpecFileEmitter.SupportedModes`) is **M3.3** (the Z80 dataset). M3.2 adds the DSL + generator + JIT vocabulary; the importer learns it at M3.3. Recorded scope line. |

**Tests that DO NOT change (and why that is the proof):**
- **The 6502 generator snapshot** — byte-identical (Ground truth E); the headline proof point.
- **Every TomHarte test** (both tiers, 1.51M cases) — behavior-identical; any change is a bug.
- **Every Klaus test** (both tiers, 96,241,367 cycles) — cycle-identical.
- **`RegeneratedSpecTests`** — `Mos6502Spec.cs` is byte-identical (no `Io`/port/halt in the 6502 spec).
- **Every interpreter `Op{XX}` body / disassembler golden / JIT differential-fuzz / chaining / SMC /
  invalidation test** — the 6502's emitted bodies, descriptors, and JIT arms are byte-identical.
- **`SyntheticRegisterSetTests` / `SyntheticDecodeStructureTests`** (M3.1a/b fixtures) — untouched; this
  plan ADDS `SyntheticPortIoTests` + `SyntheticHaltInterruptTests` beside them.

---

## TDD tasks

Each task is a red→green→refactor unit. Tests first; the 6502 invariants (full suite + the
`CPUEMULATOR_UAT=full` TomHarte + Klaus sweeps, both tiers) gate every task that could plausibly touch a
shared file. The honest scope needs ~10 tasks (vs. M3.1b's larger count) — two additive features + one
confirmation.

### Task 0 — Baseline + the no-regression anchors

- [ ] Clean `dotnet test`; record the EXACT green count (the ~1467 baseline) in the PR description.
- [ ] Record the current 6502 `Mos6502Cpu.g.cs` content hash (the byte-identical anchor, Ground truth E).
- [ ] Grep the at-risk set (`OpcodeDescriptor(`, `JitMode\.`, `JitOpClass\.`, `AddrMode\.`, `s_addrModes`,
      `ClassifyForJit`, `WithAddressSpace`, the generator-snapshot file) and confirm the authorized-changes
      table (Ground truth G) predicts every hit; an un-predicted hit is a STOP.
- [ ] Confirm `CPUEMULATOR_UAT=full` runs the TomHarte both-tier sweep + Klaus both-tier locally (the
      pure-invariant gate for the whole milestone).

**New tests:** 0. **Net:** baseline recorded.

### Task 1 — `AddrMode.IoPortImmediate`/`IoPortIndirect` + the two mirrors (additive, no behavior)

- [ ] RED: a generator test asserting a spec row with `AddrMode.IoPortImmediate` parses (and a `JitMode`
      round-trip test for the two new members).
- [ ] GREEN: add `IoPortImmediate`/`IoPortIndirect` to `AddrMode.cs`, `s_addrModes` (`SpecParser.cs:77`),
      and `JitMode` (`OpcodeDescriptor.cs:19`).
- [ ] Confirm the 6502 generator snapshot is UNCHANGED (no 6502 row names the new modes).

**New tests:** ~2. **Net:** the mode vocabulary admits I/O ports.

### Task 2 — `PortInOp`/`PortOutOp`/`HaltOp` + `Spec.PortIn`/`PortOut`/`Halt` + signatures (additive)

- [ ] RED: a generator test that a row using `PortIn("A")` is recognized (and `Halt()`).
- [ ] GREEN: add the three `Op` records (`Op.cs`), the three `Spec` factories (`Spec.cs`), and the
      `s_microOpSignatures` entries (`SpecParser.cs:38`): `PortIn`/`PortOut` → `{ Reg }`, `Halt` → empty.
- [ ] Confirm the 6502 spec/snapshot unchanged.

**New tests:** ~2. **Net:** the micro-op vocabulary admits port + halt ops.

### Task 3 — The `Port` class in the class/mode matrix (additive class)

- [ ] RED: tests that (a) a `PortIn`/`PortOut` row classifies as `InstructionClass.Port`; (b) a port op
      in a non-port mode (e.g. `Absolute`) is a CPUGEN diagnostic; (c) a port op in `IoPortImmediate`/
      `IoPortIndirect` is accepted.
- [ ] GREEN: add `InstructionClass.Port` (`SpecModel.cs`); `s_portOpKinds` + `s_portModes`
      (`SpecParser.cs`); the `ClassifyOps` branch mapping port-op rows to `Port`; the
      `ValidateModeForClass` branch requiring a `Port`-class row use a `s_portModes` mode. Add
      `JitOpClass.Port` (`OpcodeDescriptor.cs:7`) + the `ClassifyForJit` mapping (`CpuEmitter.cs:1489`).
- [ ] Confirm the 6502 matrix decisions are byte-identical (no 6502 row is a port op).

**New tests:** ~4 (positive + negative mode-legality). **Net:** the matrix admits the Port class.

### Task 4 — The interpreter port body + the `ReadIo`/`WriteIo` contract (additive emission)

- [ ] RED: `SyntheticPortIoTests.IN_reads_the_Io_bus_not_the_program_bus` +
      `OUT_writes_the_Io_bus_not_the_program_bus` + `Port_op_charges_the_Io_cycle` (Ground truth F.1) —
      compile the synthetic port CPU, place divergent bytes on the two buses, assert the port op hits
      the `Io` bus.
- [ ] GREEN: in `CpuEmitter`, add the `Port`-class body template (`EmitPortBody`) emitting:
      `IoPortImmediate` → `byte port = ReadBus(PC); PC++; A = ReadIo(port);` (for `PortIn`) /
      `WriteIo(port, A);` (for `PortOut`); `IoPortIndirect` → the port comes from a register
      (the Z80 `(C)` form — for the synthetic CPU, name `A` to keep it minimal). Emit the
      `ReadIo`/`WriteIo` REQUIREMENT into the header doc-comment + (conditionally) into the generated
      contract ONLY when the model has a `Port`-class row (Ground truth A.4 — so the 6502 is unaffected).
- [ ] Confirm the 6502 `.g.cs` byte-identical (the new template never fires for the 6502).

**New tests:** ~4 (the F.1 interpreter rows + the generate-clean row). **Net:** the interpreter targets
the Io bus.

### Task 5 — The JIT port emit arm (additive, never-fastmem) + the fastmem confirmation

- [ ] RED: `SyntheticPortIoTests.Fastmem_never_serves_the_Io_space` (structural — `Fastmem` over the
      Program bus maps no Io page) + `EmitPort_arm_hits_the_Io_bus` (direct-emit: drive the generated
      `EmitPort` primitive / a minimal `DynamicMethod` with a stub Io `IAddressSpace`; assert the callout
      lands on the Io bus, never `LoadByteFromBus`).
- [ ] GREEN: add `JitMode.IoPortImmediate`/`IoPortIndirect` handling + the `EmitPort` arm
      (`BlockCompiler.Emit.cs`, Ground truth D) — an unconditional second-`IAddressSpace` callout, NO
      fastmem branch. Add the `ArgIoBus` `BlockDelegate` parameter (additive to the delegate signature;
      the 6502's emitted blocks never reference it). Wire `EmitInstruction`'s class switch
      (`BlockCompiler.cs:183`) to dispatch `JitOpClass.Port` → `EmitPort`.
- [ ] Confirm: the 6502 JIT suite (differential fuzz, chaining, SMC, TomHarte-JIT, Klaus-JIT) green and
      byte-identical (the `ArgIoBus` parameter is unused by 6502 blocks).

**New tests:** ~3. **Net:** the JIT targets the Io bus, never fastmem.

### Task 6 — The `Halt()` micro-op body + the `Halted` hook + the `Step` halted guard (additive)

- [ ] RED: `SyntheticHaltInterruptTests.Halted_cpu_idles_one_cycle_per_step` (Ground truth F.2) —
      compile the synthetic halt CPU; after `HALT`, each `Step` advances `CycleCount` by one and does
      not fetch/advance PC.
- [ ] GREEN: emit the `Halt` case in the `Register`-class body (`CpuEmitter` `EmitRegisterBody` + the JIT
      `EmitRegisterOp`/its own arm) — the body calls the partial's halt-latch setter; emit the
      `partial bool Halted` hook + the `Step` halted guard (Ground truth B.1) CONDITIONALLY (only when
      the model has a `HaltOp`), and the `IdleCycle()`/halt-setter REQUIREMENT into the contract.
- [ ] Confirm the 6502 `Step` byte-identical (no `HaltOp` → no guard emitted).

**New tests:** ~3 (idle-per-step, generate-clean, the latch sets). **Net:** the halted state exists.

### Task 7 — The `Machine.Run` no-progress-guard accommodation (CONFIRM, not change)

- [ ] RED: `SyntheticHaltInterruptTests.Machine_Run_does_not_trip_the_no_progress_guard_on_a_halted_cpu`
      (Ground truth B.2) — a `Machine` running a halted CPU for a budget returns the consumed budget and
      does NOT throw.
- [ ] GREEN: **confirm `Machine.Run` is correct AS-IS** (the halted Step advances ≥1 cycle, so the guard
      never fires). The test should pass with NO `Machine.cs` change once Task 6 lands. If it does not,
      the bug is in Task 6's idle-cycle charge, not the guard — fix there.
- [ ] Confirm the `StuckCpu` no-progress test (the genuinely-stuck case) STILL throws (the guard still
      does its job).

**New tests:** ~2. **Net:** the guard is confirmed to handle halt correctly without a change.

### Task 8 — The JIT dispatcher halted fast path + the wake (additive dispatcher branch)

- [ ] RED: `SyntheticHaltInterruptTests.Halted_cpu_wakes_on_a_serviced_interrupt` (interpreter tier) +
      `Jit_dispatcher_halted_fast_path_idles` (direct/seam-level pin on the new `JittedCpu.Run` branch,
      Ground truth B.3).
- [ ] GREEN: add the `if (_inner.Halted) { idle via _inner.Step; continue; }` branch to `JittedCpu.Run`
      (Ground truth B.3) — one branch above the existing `InterruptPending` branch, delegating to
      `_inner.Step`. (J1 keeps the live second-CPU JIT run out of M3.2; this is the dispatcher-branch
      addition + its seam pin.)
- [ ] Confirm the 6502 JIT suite green + byte-identical (the 6502 is never halted, so the branch never
      fires).

**New tests:** ~2. **Net:** the JIT does not busy-compile a halted CPU.

### Task 9 — The non-6502 interrupt-shape proof + the IM-expressibility doc contract (CONFIRM)

- [ ] RED: `SyntheticHaltInterruptTests.Interrupt_service_vectors_through_a_table_not_a_fixed_address`
      (Ground truth F.2) — the synthetic partial's `TryServiceInterrupt` vectors through a table indexed
      by `VectorBase`, NOT a fixed `$FFFE`; assert PC lands on the table entry.
- [ ] GREEN: **no Core/generator interrupt code changes** (Ground truth C — the seam is already
      sufficient). The test passes against the EXISTING `TryServiceInterrupt`/`InterruptPending`
      partial-hook seam. Extend the generated header doc-comment contract (`CpuEmitter.cs:14-19`) with the
      IM-expressibility note (Ground truth C.3) — a comment-only change documenting the contract for the
      M3.4 author.
- [ ] Confirm the 6502 interrupt tests (NMI/IRQ, the `Mos6502InterruptTests` suite) green + byte-identical.

**New tests:** ~2. **Net:** the seam is proven to express a non-6502 interrupt shape; the contract is
documented.

### Task 10 — The M4 deferred-inputs note + final invariants

- [ ] Add the Ground-truth-G M4 note to the appropriate doc location (this plan + a short pointer in the
      ADR's open-questions or a closeout note) — code-free.
- [ ] Final gate: full suite green at the Task-0 baseline + ~34 new tests; `CPUEMULATOR_UAT=full`
      TomHarte both tiers (1.51M each) + Klaus both tiers (96,241,367) green; the 6502 `.g.cs` hash equals
      the Task-0 hash (byte-identical, Ground truth E); the generator snapshot did NOT re-snap.

**New tests:** 0. **Net:** the milestone closes with the byte-identical-6502 proof point intact.

---

## Literal code reference (the load-bearing additions, gathered)

The literal shapes the tasks realize, in one place (the Ground-truth sections give the surrounding
contract; this is the implementer's quick reference). All are ADDITIVE — none changes a 6502 path.

**`Op.cs` (+3 records):**
```csharp
public sealed record PortInOp(string Target) : Op;
public sealed record PortOutOp(string Source) : Op;
public sealed record HaltOp : Op;
```

**`Spec.cs` (+3 factories):**
```csharp
public static Op PortIn(string target) => new PortInOp(target);
public static Op PortOut(string source) => new PortOutOp(source);
public static Op Halt() => new HaltOp();
```

**`AddrMode.cs` / `JitMode` (+2 members each), `JitOpClass` (+1):**
```csharp
// AddrMode + JitMode:  …, Relative, IoPortImmediate, IoPortIndirect,
// JitOpClass:          …, Rmw, Port,   // Port: an Io-bus callout, never fastmem
```

**`SpecParser.cs` (+mirror entries):**
```csharp
["PortIn"]  = new[] { ArgKind.Reg },
["PortOut"] = new[] { ArgKind.Reg },
["Halt"]    = System.Array.Empty<ArgKind>(),
// s_addrModes += "IoPortImmediate", "IoPortIndirect"
// s_portOpKinds = { "PortIn", "PortOut" };  s_portModes = { "IoPortImmediate", "IoPortIndirect" };
// s_registerOpKinds += "Halt"   (Halt rides the Register/Implied class)
```

**The interpreter port body (CpuEmitter `EmitPortBody`, shape):**
```csharp
// IoPortImmediate, PortIn("A"):
//   byte port = ReadBus(PC); PC = (ushort)(PC + 1);   // the (n) operand byte (charges 1)
//   A = ReadIo(port);                                 // the Io bus (charges 1) — NEVER ReadBus
// IoPortImmediate, PortOut("A"):
//   byte port = ReadBus(PC); PC = (ushort)(PC + 1);
//   WriteIo(port, A);                                 // the Io bus — NEVER WriteBus
// IoPortIndirect (the Z80 (C) form): port comes from a register, no operand byte (length 1).
```

**The `Step` halted guard (CpuEmitter, emitted ONLY when the model has a HaltOp):**
```csharp
public void Step()
{
    if (TryServiceInterrupt()) return;     // services + clears the halt latch (the wake)
    if (Halted) { IdleCycle(); return; }   // idle one cycle — do NOT fetch (Ground truth B.1)
    // …normal fetch/decode/execute (byte-identical for a no-HaltOp CPU, where this guard is absent)…
}
```

**The JIT dispatcher halted fast path (`JittedCpu.Run`, +1 branch — Ground truth B.3):**
```csharp
if (_inner.InterruptPending) { /* …service via _inner.Step (UNCHANGED)… */ continue; }
if (_inner.Halted)                          // NEW: idle to the next interrupt/budget edge
{
    long before = _inner.CycleCount;
    _inner.Step();
    cycleBudget -= _inner.CycleCount - before;
    continue;
}
// …InvalidateIfDirty + GetOrCompile + RunChain (UNCHANGED)…
```

**The JIT port emit arm (`BlockCompiler.Emit.cs`, Ground truth D — never-fastmem):** see Ground truth
D's `EmitPort` literal. Key invariant: it uses the `ArgIoBus` `IAddressSpace` callout, NEVER
`LoadByteFromBus`/`EmitStoreByte`.

---

## Self-review

**Does the plan realize the brief's four items exactly?**
- (1) I/O-space micro-ops `PortIn`/`PortOut` targeting the `Io` `AddressSpace`, purely additive, synthetic
  CPU exercising both tiers hitting the Io space, JIT never-fastmems the Io space, class/mode matrix
  grows a Port class — Ground truths A, D, F.1; Tasks 1-5. ✅
- (2) Generic halted state shared by Z80 `HALT` / 68000 `STOP`, the no-progress guard accommodated, a
  `Halt()` micro-op + run-loop/guard handling, synthetic proof — Ground truth B, Tasks 6-8, F.2. ✅
- (3) Interrupt seam — assessed honestly (already generic: servicing per-CPU hand-written, line
  wired-OR, both tiers boundary-sample policy-blind), the ONE enabling change (Io-bus acknowledge
  reachability) identified, the IM-expressibility contract documented, a synthetic non-6502 (table-
  vectored) interrupt proof — Ground truth C, Task 9, F.2. ✅
- (4) M4 inputs recorded not built (wide-bus additive; 3-bit IPL nudge; 8086 IVT/shared-Io) — Ground
  truth G, Task 10. ✅

**Honest derivations present?** ✅ The Derived-scope table classifies each item refactor/additive/
confirm-only with file citations; the verdict states M3.2 is materially lighter than M3.1a/b (no
6502-shaped seam to reshape; byte-identical 6502 output, no re-snap). The interrupt-seam finding (how
much already generic) is the Ground-truth-C table. The byte-identical-6502 claim is derived in Ground
truth E with the per-artifact table and the contrast to why M3.1a/b DID re-snap. The authorized-test-
changes table enumerates the (expected-empty) existing-test moves with the snapshot-must-not-move STOP.
Test estimate ~34 new (~1467 → ~1501) is summed from the per-task rows.

**Is the byte-identical claim actually sound?** Yes, and it is the strongest part of the milestone. The
two reasons are independent: (a) every new emission path is CONDITIONAL on a `Port`/`Halt` row the 6502
lacks; (b) `OpcodeDescriptor`'s record shape is UNCHANGED (the port op rides `JitOp.Kind` +
`JitOpClass.Port`; halt rides the `Register` class) so no 6502 descriptor literal's text changes, and
the new `JitMode`/`JitOpClass` members are additive enum values that never appear in a 6502-emitted
literal. This is genuinely cleaner than M3.1a (which re-serialized every `JitOp`) and M3.1b (which
re-shaped every descriptor's `Length`).

**Where could an implementer go wrong?** Three traps, each flagged: (i) reaching for `LoadByteFromBus`
in the port arm (would index the memory fastmem by port number — Ground truth D's invariant + the F.1
divergent-bus test catch it); (ii) relaxing `Machine.Run`'s no-progress guard instead of charging an
idle cycle (Ground truth B.2 explains the guard is correct; the fix is the always-advance invariant);
(iii) trying to generalize the interrupt seam (Ground truth C proves none is needed — the work is a
doc + the Io-bus reachability).

**Scope honesty.** The plan repeatedly states what it does NOT build: no Z80/68000/8086 opcodes, no
real IM 0/1/2, no `IFF1`/`IFF2`/`EI`-delay/`I`/`R`, no wide bus, no IPL level, no public `IsHalted`
surface, the importer-loader extension deferred to M3.3, J1 deferred to M3.5. The synthetic interrupt
CPU is explicitly the minimum non-6502 SHAPE (table-vectored), not real IM 2.

**Format matches house style?** Header (goal/scope/NOT-in-scope/deviations/ADR+brief links/plan series);
derived-scope + derived-numbers; lettered ground-truth sections (A port DSL + Io-targeting; B halted
state; C interrupt seam generic-already vs newly-generalized + IM-expressibility; D JIT never-fastmem;
E byte-identical 6502; F synthetic CPUs; G M4 deferred; H rolled into G); authorized-test-changes table;
TDD tasks with checkboxes + per-task test counts; literal code; self-review. Matches the M3.1b template,
sized to the honest (lighter) scope.
