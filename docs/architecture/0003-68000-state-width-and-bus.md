# ADR 0003 — 68000 state width, the size axis, and the wide big-endian bus (Milestone M4, foundation half 1)

> **Status:** Accepted (architecture pass — not yet implemented)
> **Date:** 2026-06-15
> **Deciders:** Mark (owner); this ADR is the decision record the M4 Planner + Builder consume across the 68000 arc.
> **Supersedes / relates to:**
> - **ADR 0001** (`0001-z80-second-architecture.md`) — the ADR format, the genericity method, and the J1–J10 JIT audit this extends. ADR 0001's verdict (`§3`, "what Z80 does NOT prove") names the exact dimensions this ADR decides: `RegisterDef.Bits ≤ 16`, byte-only bus transactions, big-endian wide access.
> - **ADR 0002** (`0002-address-space-scaling.md`) — confirms the 68000's 24-bit address bus fits the current `addressBits ≤ 24` flat-page-table cap. **This ADR does not touch address-space scaling**; the 68000 forces no two-level table.
> - The forward-research brief `docs/research/68000-architecture-analysis.md` (the structural domain input; this ADR ratifies its provisional recommendations, corrected against the post-M3 `main` tree and the now-confirmed TomHarte vector schema).
> - The JIT genericity close-out `docs/superpowers/plans/2026-06-14-m3-z80-m35-3c-jit-genericity-findings.md` §7 (the M4-readiness observation: the `IJitTarget` seam accepts a third CPU as all-fallback with no JIT change; the pressure is on `Core` width + bus + decode).
>
> **Companion ADR:** `0004-68000-decode-addressing-and-exceptions.md` (decode model, the 14 EA modes, supervisor/exception machinery, importer, and the M4 PR breakdown). **0003 + 0004 together are the M4 foundation; read both.**

---

## 1. Context

### 1.1 Why an Architect pass now (the 6502 and Z80 didn't need one)

The 6502 (M1/M2) and the Z80 (M3) reused the framework's existing seams. ADR 0001 covered the Z80 as the "second architecture" proof: it stretched the **front half** of the framework — decode structure (prefix tables), register *count/identity* (data-driven names), the flag *vocabulary* (per-spec `FlagLayout`), and the block/chain model — but it stayed **8-bit, little-endian, byte-addressed, with a flat 16-bit address space**. It shares the 6502's *fundamental data/memory model*. So no foundational decision spanning multiple PRs was open: each M3 chunk extended a seam additively.

The 68000 is different. It exerts **new cross-cutting pressure on `Core` + the state model + the bus** that *every* M4 PR shares, and the decisions interlock (the size axis touches the register file, the micro-op vocabulary, the bus, the JIT emit layer, and the TomHarte gate at once). Those must be made **once, up front**, or each M4 PR will re-litigate them incompatibly. This ADR makes the half of those decisions that concern **machine state and the bus**; ADR 0004 makes the half that concern **decode, addressing, and exceptions**.

This ADR's organizing question mirrors ADR 0001's: **where is the framework still secretly ≤16-bit / byte-only / little-endian, and how does the 68000 reshape that seam additively — without changing the 6502 or Z80 by one byte?**

### 1.2 The 68000 in one paragraph (the state/data dimensions)

The Motorola 68000 is a 16/32-bit CISC processor: a **32-bit programming model** (eight 32-bit data registers `D0–D7`, eight 32-bit address registers `A0–A7`, a 32-bit `PC`) behind a **16-bit external data bus** and a **24-bit address bus**, and it is **big-endian**. `A7` is the stack pointer, **banked** into a User Stack Pointer (USP) and a Supervisor Stack Pointer (SSP) selected by the `SR.S` supervisor bit. Every data-movement and ALU operation carries a **size suffix** — `.b` (8), `.w` (16), `.l` (32) — applied to the *same* register: an axis the framework has never had. Critically, a `.b`/`.w` write to a *data* register is **partial** (`MOVE.W #x,D0` changes only `D0[15:0]`, preserving `D0[31:16]`), while a `.w` write to an *address* register is **sign-extended to the full 32 bits**. The status register `SR` is **16-bit**: the low byte is the user-visible **CCR** (`X N Z V C`, including the **X** "extend" flag — a second carry the 6502/Z80 lack), the high byte the **system byte** (trace `T`=15, supervisor `S`=13, 3-bit interrupt mask `I0–I2`=8–10). Word/long memory access is **even-aligned** (odd-address word/long access faults — decided in ADR 0004). A long access is **two** word bus cycles, high word first.

### 1.3 What the post-M3 tree actually looks like (verified against `main` @ `797c69c`)

The forward brief was drafted against an older `feat/m3-register-file` tree. The merged M3 work has since moved several seams; the decisions below are grounded against the **current** source:

- **`RegisterDef`** (`src/CpuEmulator.Core/Specification/RegisterDef.cs:12-14`) is now `record RegisterDef(string Name, int Bits, RegisterRole Role, string? HighHalf, string? LowHalf)`. The `HighHalf`/`LowHalf` fields are the **Z80 pair-view machinery** (M3.4a): a 16-bit register may be a computed *view* over two 8-bit halves, with no backing field. The parser still rejects `Bits` outside `{8, 16}` (`SpecParser.cs:497-500`, message "register width must be 8 or 16 bits"), and pair-view halves are validated to be exactly 8-bit (`SpecParser.cs:544`).
- **`RegisterRole`** (`RegisterRole.cs`) is `{ General, ProgramCounter, Status, StackPointer }` — no banking/mode concept.
- **`AccessWidth`** (`AccessWidth.cs`) **already** enumerates `Byte = 1, Word = 2, Long = 4`. `IPeripheral.Read/Write` already take an `AccessWidth` (`IPeripheral.cs:17-18`); `AddressSpace` passes `AccessWidth.Byte` on every byte access (`AddressSpace.cs:83,118`).
- **`IAddressSpace`** (`IAddressSpace.cs`) exposes only `Read8`/`Write8`/`TryPeek8` + `MapMemory`/`MapPeripheral`. **No wide transaction methods.** `AddressSpace` is a flat 256-byte-page table capped at 24 address bits (`AddressSpace.cs:34`). The JIT fastmem view `TryGetDirectAccess` (`AddressSpace.cs:131`) is a `byte[]` page + offset.
- **`ICpuCore`** (`ICpuCore.cs:38-42`) introspection is `ulong GetRegister(string)` / `SetRegister(string, ulong)` — **already wide enough for 32-bit registers** (zero-extend; the harness/monitor see 32-bit values for free).
- **`Op`** (`Op.cs`) is a closed micro-op vocabulary. **No op carries a size operand.** The Z80 added per-width *named* ops (`Add8Op`, `Add16Op`, `Inc16Op`, …) — i.e. it modelled width by *op identity*, not by a `Size` parameter.
- **The JIT** is generic post-M3 (M3.5-3a): `BlockCompiler<TCpu>` resolves all CPU specifics through the per-CPU generated `IJitTarget` (`src/CpuEmulator.Core/Jit/IJitTarget.cs`); the `CpuEmulator.Jit` assembly references only `Core`. The register-field map (`_regFields`) resolves `FieldInfo` **by name** and is **width-agnostic by construction** (it reads `FieldInfo.FieldType`); the Z80's all-fallback bring-up emits no register IL yet.

### 1.4 The confirmed TomHarte m68000 vector schema (decisive new input)

The eventual M4 acceptance gate is the SingleStepTests 68000 set. I fetched the actual upstream data (`SingleStepTests/680x0`, `68000/v1`) and confirmed the schema — this is **new ground truth** the forward brief flagged as "assumed-available, shape-unconfirmed" (its open-question 7):

- **125 files, gzipped, named by MNEMONIC + SIZE** — `ADD.b.json.gz`, `ADD.l.json.gz`, `ADD.w.json.gz`, `ANDItoCCR.json.gz`, `NOP.json.gz`, … — **not by opcode-hex** like the 6502 (`00.json`) and Z80 (`cb 00.json`). Several thousand cases per file (`NOP.json` = 8065 cases).
- **Per-case state** (`initial`/`final`): `d0`–`d7`, `a0`–`a6`, **`usp`, `ssp`** (the two A7 banks, exposed separately — there is **no `a7` field**), `sr` (full 16-bit, e.g. `9985` = `0x2701` = S-set, mask 7), `pc`, **`prefetch`** (a **2-entry word array** — the 68000's 2-word prefetch queue, checked initial AND final), and `ram` (address/value byte pairs).
- **`transactions`** array, each `[direction, ?, function-code, address, size, value]` — e.g. `["r", 4, 6, 3076, ".w", 1657]`. **The bus trace is word-granular and carries the access size as `.b`/`.w`/`.l`.** This is the single most load-bearing confirmation in this ADR: the recording bus must record *word/long* transactions, not bytes — which means the byte-only bus cannot satisfy the gate (see Decision 2).

Two findings here change the design beyond the forward brief: (a) **the prefetch queue is part of the gate** (the interpreter must model the 2-word prefetch, not just PC), and (b) **the vector files are mnemonic-keyed**, which aligns with the field-decomposition extraction shape (detailed in ADR 0004) and means the M4 TomHarte loader is structurally a *new* loader, not a parameterization of the Z80 one.

> Sources: [SingleStepTests/680x0](https://github.com/SingleStepTests/680x0), [SingleStepTests/ProcessorTests](https://github.com/SingleStepTests/ProcessorTests) (the archived monolith carrying `68000/`), [SingleStepTests/m68000](https://github.com/SingleStepTests/m68000) (MAME-microcode-generated).

---

## 2. Decisions

Each decision states the options, the chosen option, and the consequences (good and bad). The invariant on **every** decision: the 6502 and Z80 stay byte-identical (the `RegeneratedSpec` guard + their TomHarte/ZEX gates). Every change is **additive** — widening a constraint, adding a method, adding an enum member — never altering the 8-bit CPUs' generated output.

### Decision 1 — The register model: `Bits ≤ 32`, the size axis, partial vs. sign-extended writes, A7 banking

**The problem.** Three distinct sub-problems, all new:

1. **32-bit storage.** `RegisterDef.Bits` is capped at 16 (`SpecParser.cs:497`). The 68000's `D0–D7`/`A0–A7`/`PC`/`USP`/`SSP` are 32-bit.
2. **The size axis.** The *same* `D0` is operated on at three widths *by the instruction*, with **partial-write** semantics for data registers (`.b`/`.w` preserve the upper bits) and **whole-write-sign-extend** for address registers (`An.w` writes all 32 bits, sign-extended; address-register ops set **no** CCR). Width is a property of the **(instruction × micro-op)**, not of the register declaration — an axis neither the 6502 nor the Z80 has.
3. **A7 banking.** `A7` is `USP` or `SSP` depending on `SR.S`. A normal `A7` reference, and the implicit stack push/pop of exceptions and `BSR`/`JSR`/`RTS`, hit the *current-mode* bank; privileged `MOVE USP` reaches the other bank explicitly.

**Options for storage width (sub-problem 1).**

- **(A) Relax `RegisterDef.Bits` validation to `8 | 16 | 32`; type the storage field by width (`byte`/`ushort`/`uint`).** The generator already selects a field type from `Bits`; extend the selection to a third case. The introspection contract (`ulong GetRegister/SetRegister`) already covers 32-bit.
  - *Pros:* minimal, additive, the obvious generalization; the JIT `_regFields` map is already width-agnostic (reads `FieldType`); ADR 0001's "M3 NOW item 2" explicitly pre-planned `Bits` validation to become "a clean function of `Bits`, not a two-case switch."
  - *Cons:* the **op bodies** are still 8/16-bit (`unchecked((byte)…)` casts, `ushort` math). 32-bit math is genuinely new emit code — but that is Decision 1's sub-problem 2 (the size axis), not the storage typing.
- **(B) Width-suffixed register *names* (`D0`, `D0W`, `D0B` as aliasing views over one 32-bit field), reusing the Z80 `HighHalf`/`LowHalf` pair-view trick.**
  - *Pros:* reuses an existing seam.
  - *Cons:* **rejected.** The Z80 pair-view exists because the halves are *independently named in the ISA* (`B`, `C`, and `BC` are all real Z80 register names). The 68000 does **not** name `D0.w` as a separate register — the size is an *instruction field*, not a register name. Modelling it as names explodes the register table (~24 phantom names), misrepresents the silicon, and breaks the TomHarte gate (which names exactly `d0`–`d7`). The pair-view machinery is the *wrong* tool for the size axis.

**Decision: (A) for storage** — relax `Bits` validation to `8 | 16 | 32`, type the field `byte`/`ushort`/`uint` as a function of `Bits`. **Do NOT model the size axis as register names.**

**Decision for the size axis (sub-problem 2): a `Size` operand threaded through the size-bearing micro-ops, with partial-write / sign-extend semantics encoded in the op body — NOT three named ops per operation, and NOT a `Size`-keyed register name.**

Concretely, the micro-op vocabulary (`Op.cs`) grows a small `enum Size { Byte, Word, Long }` and the 68000's size-bearing ops carry it as a field. The shape (illustrative — exact records are ADR 0004 / the M4 extraction's to finalize):

```csharp
public enum OperandSize { Byte, Word, Long }   // Core/Specification

// 68000 size-parameterised ops (ONE record per operation, size as a field):
public sealed record Move68kOp(OperandSize Size) : Op;          // MOVE — sets CCR; partial write to a Dn dest
public sealed record Alu68kOp(string Op, OperandSize Size) : Op; // ADD/SUB/AND/OR/EOR/CMP — Op names the ALU fn
public sealed record AluAddr68kOp(string Op, OperandSize Size) : Op; // ADDA/SUBA/CMPA/MOVEA — NO CCR; .w sign-extends to 32
```

Two semantic rules the op body MUST encode (TomHarte will catch a violation, but only after the design is wrong — this is research-brief risk 2):
- **Data-register `.b`/`.w` is a partial write:** read the full 32-bit field, replace the low 8/16 bits, write back. The upper bits are preserved.
- **Address-register `.w` is a whole-register sign-extended write** (`Dn`/memory `.w` writing an `An`, or `MOVEA.W`): sign-extend the 16-bit result to 32 bits and write all 32. Address-register ALU/`MOVEA` set **no** CCR (directly analogous to the Z80 `INC rr` "sets no flags" quirk, ADR 0001 Decision 4 — the generator must not "helpfully" add flag writes).

**Decision for A7 banking (sub-problem 3): `RegisterRole` gains a `StackPointer`-with-banking concept modelled as ONE named register `A7` whose backing is mode-selected in the hand-written `M68000Cpu` partial.** The two physical banks `USP`/`SSP` are real 32-bit fields on the partial; `A7` is a generated/partial accessor that returns the bank `SR.S` selects. This is the same altitude ADR 0001 used for the Z80's `R`-refresh and the alternate-set swap (a mode side effect in the partial, not a micro-op). **Crucially, given the TomHarte schema (Decision 1.4): the introspection contract must expose `USP` and `SSP` as their own register names** (the vectors name `usp`/`ssp`, not `a7`). So the register file declares `A0`–`A6`, plus `USP` and `SSP` as distinct named registers, plus `A7` as the mode-selected *view* — three names backing two fields, the view resolving in the partial. `RegisterRole` likely grows a `StackPointer`-banked marker (or the banking stays entirely in the partial with `A7` as a computed property — decide just-in-time at the first M4 PR, see §4).

**Consequences.**
- *Good:* the 8-bit CPUs are untouched (the `8 | 16` path is unchanged; the size axis and banking are 68000-only constructs the 6502/Z80 specs never name). The introspection contract needs **zero** change (32-bit fits `ulong`; `USP`/`SSP`/`A7` are just names). The size axis is encoded as **data on the op**, consistent with the framework's "make it data, not a code branch" thesis.
- *Bad:* the interpreter emit arms and (eventually) the JIT emit arms gain genuinely new code — 32-bit math, partial-write read-modify-write, sign-extend-on-`An.w`, the no-CCR address-register rule. This is the largest *semantic* growth in the micro-op vocabulary across the whole project (larger than any single Z80 quirk). It is **not** a mirror-table edit. The `Size` operand is the **highest-value, highest-risk single addition** of M4 (research-brief risk 2).
- *Bad:* the flag model must carry the **X (extend)** flag distinctly (a second carry, consumed by `ADDX/SUBX/NEGX/ROXL/ROXR/ABCD/SBCD`). The post-M3 `Flag` enum already *has* an `X` member (bit 12 placeholder) and `FlagLayout` already makes bit positions per-spec data — so this is **mostly already absorbed** by the M3.4a flag work. The one nudge: `FlagBitDef.Bit` is documented "0–7 (a byte status register)"; the 68000 `SR` is **16-bit** with the supervisor byte in bits 8–15. `FlagLayout`/`FlagBitDef` must accept bit positions 0–15 (the CCR flags live in bits 0–4; the system-byte bits `S`/`T`/`I0–I2` live in 8–15). This is a one-line constraint relaxation, additive.

### Decision 2 — The bus: add `Read16/Read32/Write16/Write32` to `IAddressSpace`, with endianness as a bus property; honor word/long as one/two transactions

**The problem.** The bus is byte-only (`Read8`/`Write8`). Multi-byte values are assembled **little-endian, in the CPU emitter/JIT**, by composing byte accesses — the byte order is a CPU-side convention, not a bus property. The 68000 needs **word (16-bit) and long (32-bit) big-endian transactions**, where a word is one bus cycle and a long is two word cycles (high word first), and — confirmed in Decision 1.4 — **the TomHarte bus trace records word/long transactions with an explicit `.b`/`.w`/`.l` size**. The byte-only bus cannot produce a trace at word granularity, so it cannot satisfy the gate.

**Options.**

- **(A) Add `Read16BE/Read32BE/Write16BE/Write32BE` (or width-parameterized `Read(addr, AccessWidth)` ) to `IAddressSpace`, with endianness a bus/CPU property; the wide methods compose over the page table.** A word access is **one** call; the bus assembles it big-endian; the fastmem fast path does one wide load (`BinaryPrimitives.ReadUInt16BigEndian` over the page backing).
  - *Pros:* a word access is one transaction — matches the silicon's one-bus-cycle word and makes the **TomHarte trace (Decision 1.4) and cycle charging natural** (one charge per word, two per long); the **alignment check has one home** (the bus, where the address and the access-width are both known — ADR 0004 Decision on address-error); endianness becomes **data** (a bus property), not a 6502-ism baked into emitted IL; the fastmem fast path gets a single wide load. `AccessWidth` (`Byte/Word/Long`) already exists and `IPeripheral` already takes it — the contract has a place to land. This is the change ADR 0001's "M3 NOW item 8" explicitly told M3.2 **not to foreclose**.
  - *Cons:* a real `IAddressSpace` contract change (the "enumerated and justified" kind ADR 0001 §10 permits). Every implementation (`AddressSpace`, `TracingAddressSpace`, any test doubles) and the JIT bus arms must learn the wide path. The fastmem direct-array math gains a byte-swapped wide read. Mitigated by: the wide methods are **additive** — the 6502/Z80 keep calling `Read8`/`Write8` unchanged.
- **(B) Compose wide accesses from `Read8` with a per-CPU big-endian policy (the 68000 partial assembles `(Read8(a) << 8) | Read8(a+1)`), keeping the bus byte-only.**
  - *Pros:* **zero `IAddressSpace` change** — the cheapest M4 bus result; reuses the entire fastmem/SMC machinery byte-for-byte (ADR 0001 J4/J8, confirmed generic).
  - *Cons:* **rejected, and this is the load-bearing M4 call.** A word access becomes two byte transactions, so the **TomHarte bus trace will have byte granularity where the vectors record word granularity (`.w`) — the gate cannot pass** (Decision 1.4 makes this concrete, beyond the forward brief's prediction). The alignment check has no natural home (the bus never sees "this is a word access"). And, per ADR 0001's whole thesis: the 68000 exists in the ladder *to prove the memory/addressing half is generic* — composing from `Read8` lets M4 declare "done" while the bus is **secretly still an 8-bit little-endian bus with a CPU papering over it**. That is the 6502-shaped trap one layer down. Option (B) dodges the very thing M4 exists to prove.

**Decision: (A) — add wide big-endian transactions to `IAddressSpace`, endianness a bus property, staged so the 6502/Z80 are untouched.** Recommended surface (final shape is the M4 bus PR's to settle):

```csharp
public interface IAddressSpace
{
    // ... existing Read8/Write8/TryPeek8/MapMemory/MapPeripheral unchanged ...
    ushort Read16(uint address);            // one transaction; endianness per Endianness
    uint   Read32(uint address);            // two word transactions (high word first for BE)
    void   Write16(uint address, ushort value);
    void   Write32(uint address, uint value);
    Endianness Endianness { get; }          // LittleEndian (6502/Z80 default) | BigEndian (68000)
}
```

The default implementations for a little-endian space may compose from `Read8` (so existing little-endian consumers and the Z80's two-byte 16-bit ops are byte-identical); the big-endian path assembles high-byte-first. The JIT fastmem wide path is `BinaryPrimitives.Read{UInt16,UInt32}BigEndian` over the same `byte[]` page backing (the page model — 256-byte pages, `AddressSpace.cs:10` — does **not** change; only the element width of the access does, confirming ADR 0001 J4's "the split is generic; only the byte-only-ness was 6502-shaped").

**Note on the two-bus and Io reuse:** the M3.2 multi-bus wiring (a CPU declares the buses it owns) and the JIT's "Io never enters fastmem" rule (ADR 0001 Decision 2) are unaffected — the 68000 is von-Neumann (one program/data space) with no separate I/O space, so it owns one wide bus. The wide methods are added to the *same* `IAddressSpace` the two-bus wiring already carries.

**Consequences.**
- *Good:* one place owns word/long transactions, the alignment fault (ADR 0004), the cycle charge (one per word, two per long), and the trace granularity the gate demands. Endianness is data. The 24-bit address fits the flat page table (ADR 0002) — **no two-level table, no `uint→ulong`**.
- *Bad:* a genuine, enumerated `IAddressSpace` contract growth — the first since M1. Every bus implementer (including the test `TracingAddressSpace`, which must now record `.b`/`.w`/`.l` transactions to diff against the vectors) gains the wide path. Acceptable per ADR 0001 §10 and the explicit M3.2 pre-planning.

> **RESOLVED in M4.2 (2026-06-15).** The "final shape is the M4 bus PR's to settle" was settled by `docs/superpowers/plans/2026-06-15-m4-2-wide-be-bus.md` exactly as the recommended surface above — the four wide methods + `Endianness` landed as DEFAULT interface methods (composing over `Read8`/`Write8`), `AddressSpace` overrides them with a construction-time `Endianness` (default `LittleEndian`), and `BusAlignment.IsMisaligned` provides the detection-only alignment seam. See the M4.2 resolution note on §6 open-question 1 for the full deliver-vs-defer (the address-error EXCEPTION → M4.5; the wide-bus JIT emit arm → M6; per-byte MMIO decomposition). The 6502/Z80 stay byte-identical + byte-only and green; no 68000 instruction executes yet.

### Decision 3 — The JIT's data half: the 68000 enters as all-fallback; the wide-bus + 32-bit emit arms are deferred to M6

**The problem.** The post-M3 JIT is generic (`IJitTarget` + `BlockCompiler<TCpu>`), but its emit arms are 6502/Z80-shaped: every register access is byte-typed (`Conv_U1`), the bus arms are byte-only, the cycle model is per-byte-access. The 68000 stresses three JIT 6502-isms the Z80 left untested (research §8): **J4** (wide big-endian bus), **J-new-A** (32-bit register IL + partial-write of `.b`/`.w` into a 32-bit field), **J5** (operand-dependent cycle counts).

**Decision: bring the 68000 up through the JIT as ALL-FALLBACK first (the proven M3.5-3a discipline), and DEFER the 68000 hot-op emit arms — like the Z80's — to the post-8086 M6 cross-architecture optimization phase.** This is not a new decision so much as honoring the one the M3.5-3c findings already recorded (§5: the hot-op emitter is built **once, for all three ISAs**, in M6 — never per-arch). The 68000's contribution to M6 is to force the **data/memory half** of the emitter to be generic (wide BE bus, 32-bit math, operand-cycles), exactly as the Z80 forced the front half.

**Consequences.**
- *Good:* the `IJitTarget` seam accepts the 68000 with **zero JIT-assembly change** (confirmed in the M3.5-3c §7 readiness note); the all-fallback parity sweep is the 68000's correctness bring-up gate and the regression net for M6; the J5 cross-arm-leak guard (the generator forces `NeedsFallback = true` for all structured-CPU descriptors, `CpuEmitter.cs:3627-3636`) already protects the 68000 from accidentally taking a 6502 arm with the wrong cycle model. The 68000 register-field map is already width-agnostic by name.
- *Bad:* the JIT stays slower-than-Tier-0 for the 68000 until M6 — the explicitly accepted "thoroughness over speed-now" trade-off. No 68000 speed in M4.
- *Deferred to M6 (recorded for the optimization phase, not decided now):* the wide-BE-bus emit arm; 32-bit IL math with partial-write RMW; the X-flag IL slot (J-new-C); operand-dependent cycle charging (J5 — `MOVEM` register count, `MULU/DIVU`, two-cycles-per-long); the synchronous-exception block-exit (ADR 0004).

---

## 3. What the 68000 proves about genericity (the data/memory half)

ADR 0001's verdict named three dimensions the Z80 leaves untested; this ADR's decisions are what prove them:

- **Register width > 16 bits** — Decision 1 takes `Bits` to 32, makes the size axis data, and proves the field-typing path was never `byte`-only by accident.
- **Wider-than-byte, big-endian, alignment-checked bus transactions** — Decision 2 is the load-bearing one. Confirmed beyond the brief by the TomHarte trace recording word/long `.w`/`.l` transactions: the byte-only bus *cannot* pass the gate, so Option (A) is forced by the oracle, not just by aesthetics.
- **The flat-`uint`-address assumption** is deliberately **left intact** — the 68000 is flat (just big-endian and word-accessed). Segmentation is the 8086's job (M5). Correct division of labour: do not conflate them, and do not pre-build 32-bit address support (ADR 0002).

Together with ADR 0001 (front half) and the M5 8086 ADR-to-come (addressing half), the three architectures leave essentially no genericity dimension untested before the M6 optimization.

---

## 4. Decisions deliberately left "just-in-time" (decide at the first M4 PR)

Honesty per the brief: where a decision genuinely depends on what the first M4 PR reveals, it is scoped here rather than over-specified.

1. **The exact `RegisterRole`/`A7` banking representation** — whether `A7` is a new `RegisterRole.BankedStackPointer` understood by the generator, or stays entirely in the partial as a computed property over `USP`/`SSP` with only `USP`/`SSP`/`A6`…`A0` declared. Both satisfy the TomHarte schema (which names `usp`/`ssp`). Decide when the first register-file PR confronts the generated-introspection-vs-partial-accessor trade-off. *Recommendation leaning:* keep banking in the partial (the ADR 0001 altitude for mode side effects); declare `USP`/`SSP` as real registers; expose `A7` as a partial accessor.
2. **The precise `Size` operand placement** — whether `OperandSize` is one field on a handful of broad ops (`Move68kOp`, `Alu68kOp`) or a wider refactor of the operand model. ADR 0001 J10 already calls for an extensible operand model; the 68000's `Size` + bit-number + register-mask + shift-count operands (ADR 0004) confirm it. The *shape* of the extensible operand model is best settled when the first ALU-family PR has real encodings in hand.
3. **Whether the prefetch queue is modelled in the interpreter from PR 1 or staged** — the TomHarte gate checks `prefetch` (2 words). The interpreter must eventually model the 2-word prefetch to pass. Whether that lands in the Core-state PR or the first instruction-family PR is a sequencing call for the Planner; flag it now so it is not a late surprise (it was absent from the forward brief).

> **M4.1 resolution (2026-06-15, `docs/superpowers/plans/2026-06-15-m4-1-core-width-and-68000-state.md`):**
> - Item 1 (A7 banking): RESOLVED as the recommendation — USP/SSP are real 32-bit registers (USP General,
>   SSP the StackPointer role); A7 is a hand-written mode-selected property on the M68000Cpu partial (the
>   SR S-bit selects USP vs SSP). RegisterRole gains NO banking member.
> - Item 2 (OperandSize placement): the `OperandSize { Byte, Word, Long }` enum landed in Core (M4.1) as a
>   standalone type — the size axis as a name. Threading it onto the size-bearing ops (Move68kOp/Alu68kOp/
>   AluAddr68kOp) is deferred to the first ALU-family PR (M4.5a) per this ADR's own recommendation, when
>   real encodings settle the extensible-operand-model shape.
> - Item 3 (prefetch-queue timing): unchanged — deferred to the interpreter PR (M4.5), out of M4.1 scope.
> - **Pipeline deviation (Decision D4):** the M4.1 68000 spec is a GUARDED HAND-WRITE
>   (`src/CpuEmulator.Cpus.M68000/M68000Spec.cs`), not importer-generated. The importer hard-rejects a
>   zero-row opcode dataset (`OpcodeDataset.Parse` throws on an empty array — the guard that protects the
>   6502/Z80 from an accidentally-empty real dataset), and the M4.1 68000 has zero instruction rows. The
>   hand-write matches the exact importer-output shape (Registers + FlagLayout + empty Instructions, no
>   DecodeStructure) with a `TODO(M4.4)` to fold it into the importer pipeline when the field-pattern
>   dataset + register-only-CPU support land. No importer code or dataset files were added in M4.1, and the
>   `M68000RegeneratedSpecTests` byte-identity guard is deferred to M4.4 (there is no importer run to guard).

---

## 5. Consequences summary

- **Additive Core changes (enumerated, justified):** `RegisterDef.Bits` validation `8|16` → `8|16|32`; field-type selection gains a `uint` case; `FlagBitDef.Bit` range `0–7` → `0–15`; `IAddressSpace` gains `Read16/Read32/Write16/Write32` + `Endianness`; `RegisterRole` may gain a banked-SP marker (just-in-time). **None changes the 6502 or Z80 generated output** (the `RegeneratedSpec` guard + TomHarte/ZEX gates stay green).
- **New micro-op vocabulary:** an `OperandSize` axis + the size-bearing 68000 ops, with partial-write / sign-extend / no-CCR-on-address-reg semantics in the bodies. (The full op list is ADR 0004's / the extraction's.)
- **The bus** carries word/long big-endian transactions; the test `TracingAddressSpace` records them with size, to diff against the mnemonic-keyed gzipped vectors.
- **The JIT** accepts the 68000 as all-fallback now; the 32-bit/wide-BE/operand-cycle emit arms are M6 (built once across three ISAs).
- **No address-space scaling** (ADR 0002 holds — 24-bit fits the flat table).

---

## 6. Open questions for the owner

1. **The wide-bus contract change (Decision 2) — owner sign-off.** This is the load-bearing M4 decision and the first `IAddressSpace` growth since M1. The TomHarte trace evidence (word/long `.w`/`.l` transactions) makes Option (A) effectively forced — but it is a real contract change touching every bus implementer. *Recommend: accept (A).*

   > **M4.2 resolution (2026-06-15, `docs/superpowers/plans/2026-06-15-m4-2-wide-be-bus.md`):**
   > - The wide surface landed as Decision 2's recommended shape: `IAddressSpace` gains `Read16/Read32/
   >   Write16/Write32` + `Endianness Endianness { get; }` as DEFAULT interface methods composing over
   >   `Read8`/`Write8` (so every implementer, incl. the test `TracingAddressSpace`, gets a correct wide
   >   path for free); `AddressSpace` overrides with a construction-time `Endianness` (default `LittleEndian`;
   >   the 68000 constructs `BigEndian` in M4.5) + the page-path composition. BE writes high-byte-first; a
   >   long is two words, high word first. The 6502/Z80 stay byte-identical + byte-only (LE default; nobody
   >   calls the wide methods).
   > - Odd-address handling (Decision 2's "the alignment check has one home"): M4.2 ships the DETECTION
   >   predicate `BusAlignment.IsMisaligned(addr, width)` (pure, no raise). The address-error EXCEPTION
   >   (vector 3, the supervisor stack frame) is DEFERRED to M4.5 per ADR 0004 Decision 3 — the M4.5
   >   interpreter checks the predicate BEFORE a wide access and vectors.
   > - A wide MMIO access composes into per-byte peripheral callouts each `AccessWidth.Byte` (Decision D5 of
   >   the M4.2 plan) — the conservative correct M4.2 behaviour (no peripheral in any current/M4.5 fixture;
   >   the TomHarte `ram` is memory). A single width-tagged MMIO callout is a future device-modelling concern.
   > - The wide-bus / 32-bit JIT emit arm is NOT in M4.2 — it is M6 (Decision 3); M4.2's wide accessors go
   >   through the normal `AddressSpace` wide path. The fastmem `TryGetDirectAccess` view is untouched.
2. **Cycle-accuracy bar (shared with ADR 0001 open-q 5, extended).** Hold the interpreter to the TomHarte per-cycle/per-transaction bus-trace + `prefetch` fidelity (the oracle demands it), with the cycle *count* computed from a 68000 timing model (mode + size + operands), not a constant `BaseCycles`. Confirm `PageCrossPenalty` generalizes to a per-arch timing addend (J5). *Recommend: yes, interpreter held to the full gate.*
3. **Prefetch-queue modelling timing** (just-in-time item 3) — Core-state PR or first instruction-family PR?

---

*End of ADR 0003. The decode model, the 14 addressing modes, the supervisor/exception machinery, the importer/extraction shape, and the M4 PR breakdown are in the companion ADR 0004.*
