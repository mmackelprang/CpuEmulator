# ADR 0001 — Z80 as CpuEmulator's Second Architecture (Milestone M3)

> **Status:** Proposed (architecture pass — not yet implemented)
> **Date:** 2026-06-13
> **Deciders:** Mark (owner); this ADR is the decision record M3's per-chunk plans consume.
> **Supersedes / relates to:** the framework design spec
> (`docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`, §7 Z80 reality-check,
> §9 milestone M3 item 10, §10 success criteria) and the research doc
> (`docs/research/emulation-framework-research.md`, §7 per-CPU notes).

---

## 1. Context

### 1.1 Why M3 exists, and the strategic goal behind it

The framework's headline claim is **CPU-agnosticism**: one constrained-DSL spec table feeds a
Roslyn source generator that emits five artifacts (state struct, Tier-0 interpreter, JIT
descriptor table, disassembler, assembler), and a separate `CpuEmulator.Jit` assembly walks the
generated descriptor table to emit IL — *without* per-CPU code in the compiler. M1 and M2 proved
this end-to-end for exactly **one** architecture, the MOS 6502. A framework validated against a
single ISA has not been validated as a framework at all; it has been validated as a 6502 emulator
with extra indirection.

The success criterion for M3 is stated plainly in the design spec §10:

> **M3:** the Z80 lands with zero — or enumerated and justified — changes to `CpuEmulator.Core`.

The **strategic** reason the Z80 is next (and the throughline of this ADR) is the milestone that
*follows* M3: a cross-architecture **JIT optimization** pass. The owner's explicit concern is that
this optimization must be **valid across architectures, not 6502-shaped**. Today's JIT was written,
tested, and tuned against the 6502 only. Before we optimize it we must discover and excise every
place it secretly assumes "6502" — otherwise we will optimize a 6502 emulator and *call* it a
framework optimization. **The Z80 is the chisel we use to find those assumptions.** Every framework
change the Z80 forces is, per spec §9 item 10, "measured and treated as a finding, not a failure."

This ADR's organizing question, applied to every seam, is therefore:

> **Where is the current framework secretly 6502-shaped, and how does the Z80 reshape that seam?**

### 1.2 What the framework looks like today (the 6502-shaped baseline)

The pieces this ADR reasons about, with file citations:

- **The spec DSL** (`src/CpuEmulator.Core/Specification/`): `AddrMode` (13 members, all 6502;
  `AddrMode.cs:6`), `Reg` (exactly `A, X, Y, S`; `Reg.cs:5`), `Flag` (6502 P-register bit
  positions `C Z I D V N`; `Flag.cs:6`), `RegisterRole` (`General, ProgramCounter, Status,
  StackPointer`; `RegisterRole.cs:3`), `RegisterDef` (`Bits` must be 8 or 16; `RegisterDef.cs:6`),
  the `Op` micro-op records (`Op.cs`) and their `Spec` factories (`Spec.cs`), and
  `InstructionDef(byte Opcode, ...)` — **a single opcode byte** (`InstructionDef.cs:7`).
- **The generator** (`src/CpuEmulator.Generators/`): `SpecParser` with its **MIRROR TABLES** block
  (`SpecParser.cs:15-145` — `s_addrModes`, `s_regMembers`, `s_flagMembers`, `s_microOpSignatures`,
  the per-class mode sets, the index-register convention), `SpecModel`/`InstructionClass`
  (`SpecModel.cs:9`), and `CpuEmitter` (`CpuEmitter.cs`, ~1476 lines) which emits the interpreter
  bodies, the disassembler, the assembler/monitor support, and `EmitJitDescriptors`
  (`CpuEmitter.cs:1304`) — the `OpcodeDescriptor[]` table.
- **The bus** (`src/CpuEmulator.Core/AddressSpace.cs`): page-table-backed, with a
  `TryGetDirectAccess` fastmem seam (`AddressSpace.cs:131`); `AddressSpaceKind` already enumerates
  `Program, Data, Io` (`AddressSpaceKind.cs:8`).
- **The interrupt model**: `IInterruptLine`/`InterruptLine` wired-OR
  (`IInterruptLine.cs`, `InterruptLine.cs`), and the 6502's interrupt servicing
  hand-written in the partial (`Mos6502Cpu.cs:69-100`).
- **The JIT** (`src/CpuEmulator.Jit/`): `OpcodeDescriptor`/`JitOpClass`/`JitMode`/`JitOp`
  (`Core/Jit/OpcodeDescriptor.cs`), `BlockCompiler` + `.Emit` + `.Flow` + `.Decimal`,
  `Fastmem`, `ChainTable`, `JittedCpu`, `CompiledBlock`/`BlockDelegate`.
- **The extraction pipeline** (`tools/CpuEmulator.SpecImporter/`): `OpcodeDataset`,
  `SemanticsMap` (with `FactoryArity`, `SemanticsMap.cs:38`), `SpecFileEmitter` (with
  `SupportedModes`, `SpecFileEmitter.cs:41`); runbook in `docs/user-guide/extraction-runbook.md`.

### 1.3 The Z80 in one paragraph (what makes it different)

The Z80 is a binary-compatible superset of the Intel 8080 with a much larger instruction set
realized through **prefix opcodes**: the base page (unprefixed), `CB` (bit/rotate/shift), `ED`
(extended: block ops, 16-bit ALU, I/O, interrupt-mode control), and the index prefixes `DD`/`FD`
(re-interpret `HL` as `IX`/`IY`, with a displacement byte for indexed modes). The compound
`DDCB`/`FDCB` forms put the **displacement byte *before* the opcode byte** (`DD CB dd op`), which
no single-byte decoder can express. The register file is **8-bit registers that pair into 16-bit
registers** (`B+C=BC`, `D+E=DE`, `H+L=HL`, plus `SP`, `IX`, `IY`, `PC`), a **second alternate set**
(`AF'/BC'/DE'/HL'`) swapped wholesale by `EX`/`EXX`, and two special 8-bit registers `I`
(interrupt vector base) and `R` (memory-refresh counter, which software can read). The flag
register `F` carries **more flags than the 6502** — `S Z Y H X P/V N C` — including a half-carry
`H` (used by `DAA`), the parity/overflow dual-use `P/V`, an add/subtract `N` flag, and two
**undocumented** flags `X`/`Y` (bits 3 and 5, copied from results) that the SingleStepTests vectors
*do* check. The Z80 has a **separate 16-bit-addressed I/O space** reached by `IN`/`OUT`, three
interrupt modes (`IM 0/1/2`) plus a non-maskable interrupt with a fixed `0x0066` vector. Counting
prefixes, the documented opcode space is **~1000+ encodings**.

Every one of those italicized words is a place the 6502-shaped framework will resist.

---

## 2. Decisions

Each decision below states the options, a recommendation, and — the throughline — the
**genericity implication**: what the Z80 proves (or fails to prove) about the framework being
CPU-agnostic rather than 6502-shaped.

### Decision 1 — Prefix-opcode decode → descriptor model

**This is the central decode decision and the one most likely to expose a 6502 assumption.**

**The problem.** The entire decode→descriptor→emit pipeline is keyed on a **single opcode byte**:

- `InstructionDef(byte Opcode, ...)` carries one byte (`InstructionDef.cs:7`); the parser rejects
  `opcode` outside `0x00..0xFF` (`SpecParser.cs:354`).
- `EmitJitDescriptors` builds a **256-slot** array indexed by that byte, filling gaps with
  `OpcodeDescriptor.Undefined` (`CpuEmitter.cs:1316-1322`).
- The interpreter's `Execute` is a `switch (opcode)` over the byte with per-opcode `Op{XX}()`
  methods (`CpuEmitter.cs:127-137`); `Step` does exactly one `ReadBus(PC); PC++; Execute(opcode)`
  (`CpuEmitter.cs:98-105`).
- The JIT's block discovery reads **one byte** and indexes the 256-slot table directly:
  `byte opcode = _bus.Read8(pc); OpcodeDescriptor d = Mos6502Cpu.JitDescriptors[opcode];`
  (`BlockCompiler.cs:80-84`), advancing PC by `d.Length`.
- The monitor's `InstructionLength`/`Disassemble`/`TryAssemble` all `switch` on the single byte
  (`CpuEmitter.cs:1014, 1253, 1062`).

The Z80's `CB`/`ED`/`DD`/`FD` prefixes mean the *opcode* is a multi-byte key, and `DDCB dd op`
interleaves a displacement byte. A 256-entry table indexed by `PC[0]` cannot represent
`ED 0xB0` (`LDIR`) distinctly from `0xB0` (`OR B`).

**Options.**

- **(A) Nested prefix tables.** Model the opcode space as a small set of tables — base, `CB`,
  `ED`, `DD`, `FD`, `DDCB`, `FDCB` — each up to 256 entries. Decode walks: read a byte; if it is a
  known prefix, switch tables and read the next byte (for `DDCB`/`FDCB`, read the displacement,
  then the opcode, into the compound table). The descriptor model becomes "table id + final byte."
  - *Pros:* mirrors the silicon's own decode; each table stays a dense ≤256 array (cache-friendly,
    the existing emission shape); the displacement-then-opcode oddity is contained in two compound
    tables; matches how every serious Z80 emulator (and MAME) decodes.
  - *Cons:* the DSL's `InstructionDef` must grow a prefix/table dimension; the parser's
    single-byte-range check, the generator's 256-slot emission, the JIT's `JitDescriptors[opcode]`
    index, and the monitor switches all change. This is the largest single mechanical change.

- **(B) Multi-byte key via a decode state machine.** A generic front-end decoder consumes bytes
  until it has resolved a full opcode, producing a canonical multi-byte **key** (e.g. a `uint`
  packing up to 4 bytes, with a length). The descriptor table becomes a dictionary keyed on that
  key (or a flat array indexed by a computed key for density).
  - *Pros:* fully general — extends to the 8086's variable-length prefixed encoding and the 68000's
    word-stream decode later, which is exactly the "don't be 6502-shaped" goal; the decoder is one
    CPU-agnostic component the generator configures.
  - *Cons:* a dictionary lookup per instruction is slower than an array index in the hot interpreter
    loop; the displacement byte (`DDCB dd op`) is *data*, not part of the opcode key — the key must
    exclude it while decode still consumes it positionally; more upfront design than (A).

- **(C) Prefix as a descriptor attribute on a flat table.** Keep one table but widen the key to a
  16-bit (prefix<<8 | op) index, with the prefix byte carried as a field on `OpcodeDescriptor`.
  - *Pros:* smallest schema delta; the table stays an array (indexed by the 16-bit key).
  - *Cons:* a 64K-slot array that is ~98% `Undefined` is wasteful; `DDCB`/`FDCB`'s displacement
    still does not fit a `prefix<<8|op` key (the displacement sits *between* prefix and opcode);
    really a degenerate (A) without the clean per-table boundaries.

**Recommendation: (A) nested prefix tables, with the decode walk modeled as a generic, generated
*prefix map* the spec declares.** Concretely:

- Extend the DSL so a spec declares its prefix structure once — e.g. an optional `Prefixes` table
  naming each prefix byte and whether it takes a leading displacement (`DD CB` → displacement
  before opcode). The 6502 declares no prefixes and the existing single-table path is the
  zero-prefix special case (so the 6502 spec and its generated output **do not change** — a
  genericity win we can assert with the existing snapshot tests).
- Add an opcode "page" / table id to `InstructionDef` (default = base page) so a row can say
  `Insn(Prefix.ED, 0xB0, "LDIR", ...)`.
- The generator emits one descriptor table **per page** (each ≤256), plus a generated **decode
  function** the interpreter's `Step` and the JIT's `Discover` both call: it consumes the prefix
  byte(s) and (for compound forms) the displacement, returning `(pageId, finalOpcode,
  displacement, totalPrefixLength)`. The JIT then indexes `JitDescriptors[pageId][finalOpcode]`.
- `OpcodeDescriptor.Length` already drives PC advancement in discovery (`BlockCompiler.cs:84`);
  it must now count prefix + displacement + operand bytes. `Discover` reads the **decode
  function's** total length, not a single `d.Length`, so the block walk stays correct.

**Genericity implication (high).** This is the seam that most loudly says "6502" today — a literal
`[256]` and `switch(opcode)` everywhere. Reshaping it to a generated per-page table + a generated
decode walk is the difference between "the framework decodes 6502 opcodes" and "the framework
decodes a *declared* opcode structure." Crucially, option (A) generalizes forward: the 8086's
prefix bytes (`0x66`/`0x67`/segment/`REP`) and the 68000's word-granular decode are both
expressible as "the spec declares its decode structure; the generator emits the walk." If we pick
the narrow fix (C) we will pay this cost *again* at the 8086 and will not have learned the lesson.
**This decision, more than any other, is what makes the later JIT optimization architecture-valid:
block *discovery* (the thing the optimizer reasons about — where blocks start and end, how PC
advances) stops being byte-indexed and becomes decode-driven.**

### Decision 2 — I/O address space

**The problem.** The Z80's `IN A,(n)` / `OUT (n),A` / `IN r,(C)` / `OUT (C),r` address a
**separate 16-bit I/O space**, distinct from the memory space. The bus already anticipates this:
`AddressSpaceKind` has an `Io` member (`AddressSpaceKind.cs:12`, with a comment naming Z80/8086),
and `AddressSpace` is constructed with a `kind` and `addressBits` (`AddressSpace.cs:30`). What is
*missing* is (a) a machine wiring a second `AddressSpace(Io, 16)` and handing it to the CPU,
(b) micro-ops that target it, and (c) a JIT guarantee that I/O is **never fastmemmed**.

**Options for the CPU↔I/O-bus wiring.**

- **(A) Z80 owns a second `IAddressSpace` for I/O, injected at construction.** The Z80's
  hand-written partial takes *two* buses (program/data is one space on the Z80 — it is von Neumann,
  not Harvard — plus the I/O space) and routes `IN`/`OUT` micro-ops to the I/O bus.
  - *Pros:* reuses the entire `AddressSpace` machinery (paging, peripheral mapping, strict mode,
    open-bus) for I/O for free; matches MAME/QEMU's separate-space model; the `Io` kind already
    exists for exactly this.
  - *Cons:* `ICpuCore` and the generated bus-wiring contract assume a single bus today
    (`Mos6502Cpu` takes one `IAddressSpace`, `Mos6502Cpu.cs:20,26`). The generated `ReadBus`/
    `WriteBus` helpers (the hand-written partial provides them, `Mos6502Cpu.cs:111-121`) are
    single-bus. A Z80 needs `ReadIo`/`WriteIo` too.

- **(B) One bus with an I/O window.** Map I/O ports into a high region of the single space.
  - *Pros:* no second bus.
  - *Cons:* wrong — the Z80's I/O space genuinely overlaps memory addresses (port `0x00` is not
    memory `0x00`); it breaks any real peripheral map; rejected.

**Recommendation: (A).** Define the I/O bus as a second `AddressSpace(AddressSpaceKind.Io,
addressBits: 16)` the machine builds and the Z80 partial holds. Introduce I/O micro-ops
(`InPort`, `OutPort`, plus the `(C)`-indexed forms — see Decision 4) whose generated interpreter
bodies call the Z80 partial's `ReadIo`/`WriteIo` (which charge the Z80's I/O cycle timing — `IN`/
`OUT` are 11/12-cycle instructions with a distinct bus pattern). The fastmem question is the
load-bearing JIT half:

- **The JIT fastmem must NEVER fastmem I/O — always a callout.** Today `Fastmem` is built from the
  *memory* `AddressSpace` only (`Fastmem.cs:23-47`, `bus.TryGetDirectAccess` per page). I/O is a
  *different bus object*; it is never in the `Fastmem` page table, so an I/O micro-op cannot
  accidentally take the direct-array arm. The rule we must enforce in the emit arm is simply: the
  `InPort`/`OutPort` emit arms call the I/O bus's `Read8`/`Write8` **unconditionally** (a plain
  callout), never the `LoadByteFromBus`/`EmitStoreByte` fastmem-branch helpers
  (`BlockCompiler.cs:277, 325`). I/O reads/writes are observable side effects (a UART transmit, an
  interrupt-acknowledge), so they must hit the device every time and in cycle order — the same
  reason MMIO never inlines (research §5). No rework of `TryGetDirectAccess` itself is needed; it
  describes one bus and we simply do not point it at the I/O bus.

**Genericity implication (medium).** The `Io` kind was added speculatively in M1 "because
8051/Z80/8086 demand it" (design spec §4). M3 is where that speculative seam either pays off or is
revealed as mis-shaped. The honest finding to watch for: the *contract* (`ICpuCore`, the
generated bus-wiring) currently bakes "one bus." Generalizing the CPU↔bus wiring to "a CPU declares
the buses it owns" (program, optional data, optional I/O) is a small but real `Core` change — and
exactly the kind of enumerated-and-justified change §10 anticipates. It also pre-pays the 8051
(Harvard `Data` space) and 8086 (`Io`).

### Decision 3 — Register & flag model deltas

**The problem.** The register/flag vocabulary is hardcoded 6502 in four mirrored places:

- `Reg` enum is `{ A, X, Y, S }` (`Reg.cs:5`); the parser mirrors it as `s_regMembers`
  (`SpecParser.cs:82-85`); `CpuEmitter.RegIndex` maps `A=0,X=1,Y=2,S=3` (`CpuEmitter.cs:1467`);
  and the JIT bakes exactly six `FieldInfo`s `FA/FX/FY/FS/FP/FPC` with a `RegField` switch over
  indices 0–3 (`BlockCompiler.cs:37-42, 454-458`).
- `Flag` enum is the 6502 P bit layout `C=0,Z=1,I=2,D=3,V=6,N=7` (`Flag.cs:6`); mirrored as
  `s_flagMembers` (`SpecParser.cs:88-91`) and `CpuEmitter.FlagBit` (`CpuEmitter.cs:458-467`).
- `RegisterDef.Bits` is constrained to 8 or 16 (`RegisterDef.cs`, enforced `SpecParser.cs:254`),
  and the emitter types an 8-bit register as `byte` and 16-bit as `ushort` (`CpuEmitter.cs:39`).
  Register *ops* (`Increment`, `Decrement`, `SetNZ`) emit `unchecked((byte)...)` unconditionally
  (`CpuEmitter.cs:300-318`) — **8-bit is assumed in the op bodies**, not just the storage.

The Z80 needs: **16-bit register pairs** that are *also* addressable as 8-bit halves
(`B`/`C`/`BC`); **16-bit ALU** (`ADD HL,rr`, `ADC/SBC HL,rr`, `INC/DEC rr`); the **alternate set**
+ `EX`/`EXX` (wholesale swaps of `AF/BC/DE/HL` with `AF'/BC'/DE'/HL'`, and `EX DE,HL`, `EX (SP),HL`);
the special registers **`I`** and **`R`** (with `R`'s low-7-bit increment-per-fetch behavior); and
a **richer flag word** `S Z Y H X P/V N C` including half-carry `H` (for `DAA`) and the
**undocumented `X`/`Y`** bits that TomHarte checks.

**Options for register pairs.**

- **(A) Model 8-bit halves as the storage; pairs are a generated view.** Declare `B, C, D, E, H,
  L, A, F` (8-bit) + `SP, IX, IY, PC` (16-bit); the generator synthesizes `BC`/`DE`/`HL` as
  computed `ushort` accessors over the halves. Micro-ops that name `BC` read/write the pair view.
  - *Pros:* matches the silicon (the halves are the registers; the pair is a concatenation); 8-bit
    ops on `C` and 16-bit ops on `BC` both work without aliasing bugs; `EXX` swaps the eight
    half-fields. Introspection (`GetRegister("BC")`) is a generated computed property.
  - *Cons:* the generator must understand "this 16-bit name is a pair of these two 8-bit names" — a
    new `RegisterDef` relationship the DSL must express.

- **(B) Model pairs as the storage; halves are a view.** Declare `BC, DE, HL` (16-bit); `B` is
  `BC >> 8`.
  - *Pros:* 16-bit ALU ops are natural.
  - *Cons:* the high/low split for 8-bit ops is error-prone; `F`'s flag manipulation wants the
    8-bit `F` to be first-class (the `Status` role is a single register). Slightly worse fit.

- **(C) A flat set with explicit alias metadata** — declare all of `B,C,BC,...` and tell the
  generator `BC` aliases `(B,C)`.
  - Equivalent to (A) with more ceremony; (A) is cleaner.

**Recommendation: (A) — 8-bit halves are storage, pairs are generated views — and grow the role
and flag vocabulary explicitly.** Specifically:

- **`Reg`/`s_regMembers`/`RegIndex`/the JIT `RegField`** all expand to the Z80 register set. The
  honest finding: `Reg` being a *closed enum* shared by Core and mirrored in the generator means
  **adding a register is a Core change plus three mirror-table edits** (`SpecParser.cs:82`,
  `CpuEmitter.cs:1467`, and the JIT's baked-`FieldInfo` set). This is the single clearest "secretly
  6502-shaped" smell: a truly CPU-agnostic framework would carry register identity as *data from
  the spec*, not a Core enum. M3 should either (i) widen the enum and accept the mirror cost as a
  measured finding, or (ii) — better for the optimization goal — **replace the `Reg` enum with
  spec-declared register names**, so the generator and JIT key on the spec's `Registers` table
  rather than a fixed enum. Recommendation: do (ii) as a deliberate M3 framework change, because the
  JIT optimization must reason about *arbitrary* register files, not `A/X/Y/S`.
- **`RegisterRole`** grows: the Z80 needs no new *roles* for the main set (PC, SP, Status all map),
  but `I`/`R` are special — model them as `General` 8-bit registers with the `R`-refresh increment
  handled in the hand-written partial's fetch path (it is a fetch side effect, not a micro-op).
  The **alternate set** is best modeled as eight more `General` 8-bit registers (`A_, F_, B_, ...`)
  with `EX`/`EXX` as dedicated micro-ops (Decision 4) that swap fields — *no* new role needed.
- **`RegisterDef.Bits` (8 or 16)** already covers the Z80; no 32-bit register appears until the
  68000, so the existing constraint holds. But **the op bodies' hardcoded `(byte)` casts**
  (`CpuEmitter.cs:303,309`, the `SetNZ` mask `0x7D`, etc.) are 8-bit-only. 16-bit `INC rr`/`ADD
  HL,rr` need width-aware emission — the emitter must consult the target register's `Bits` and emit
  `ushort` math + 16-bit flag rules. **This is genuinely new code in the emitter and the JIT, not a
  mirror-table edit.**
- **`Flag`** expands to the Z80 layout. The 6502 `SetNZ`-style flag micro-ops
  (`CpuEmitter.EmitRegisterOp` "SetNZ", `CpuEmitter.cs:312`) bake the 6502's "N=bit7, Z, mask
  `0x7D`" convention. The Z80's flag-setting is per-instruction-family and far richer (e.g. `H`
  half-carry depends on nibble carry; `P/V` is parity for logic ops but overflow for arithmetic;
  `X`/`Y` copy result bits 3/5). The clean model is a **family of flag micro-ops** — `SetSZ`,
  `SetParity`, `SetHalfCarry`, `SetXY`, `SetOverflow`, `SetAddSub(N)` — composable per instruction,
  rather than one monolithic `SetNZ`. This is the largest *semantic* growth in the micro-op
  vocabulary (see Decision 4).

**Genericity implication (high).** The register/flag model is the second-loudest 6502 assumption
(after single-byte decode). The `Reg` enum, the `A=0,X=1,Y=2,S=3` index mapping, and the JIT's six
baked `FieldInfo`s are a hard dependency on the 6502 register file *names and count*. The JIT
optimization (register hoisting into IL locals, the design spec §6 "architectural state hoisted
into IL locals at block entry") is described generically but is implemented against
`FA/FX/FY/FS/FP/FPC`. **If we optimize register allocation now, we will optimize for six 6502
registers.** The Z80's ~14+ live registers (with pairs and the alternate set) force the JIT to
treat the register file as *data* — which is precisely the precondition for an
architecture-valid hoisting/allocation optimization.

### Decision 4 — Micro-op vocabulary deltas

**The problem.** The micro-op vocabulary (`Op.cs` / `Spec.cs` / `s_microOpSignatures`
`SpecParser.cs:33-70` / `FactoryArity` `SemanticsMap.cs:38`) and the addressing-mode set
(`AddrMode.cs`) are the 6502's. The generator's **class/mode matrix** (`InstructionClass` in
`SpecModel.cs:9`; `ClassifyOps` + `ValidateModeForClass` in `SpecParser.cs:473-646`; the per-class
mode sets `s_loadAluModes`/`s_storeModes`/`s_rmwModes` `SpecParser.cs:130-145`) encodes 6502 rules:
e.g. "register-class ops require Implied mode," "Jump requires Absolute or Indirect," "the
X-indexed mode requires a register named exactly `X`" (`RequiredIndexRegister`, `SpecParser.cs:582`).

**New addressing modes the Z80 needs** (extending `AddrMode` + the parser mirror `s_addrModes`
`SpecParser.cs:72` + the JIT's `JitMode` mirror `OpcodeDescriptor.cs:19` + `SupportedModes`
`SpecFileEmitter.cs:41` + `OpcodeDataset.ValidModes` `OpcodeDataset.cs:37`):

| Z80 mode | Shape | Stresses |
|---|---|---|
| `RegisterIndirect` (`(HL)`, `(BC)`, `(DE)`) | EA = a register pair | new EA source: a 16-bit register, not an operand byte |
| `Indexed` (`(IX+d)`, `(IY+d)`) | EA = `IX/IY + signed d` | the `DD`/`FD` prefix + displacement byte (ties to Decision 1) |
| `ImmediateExtended` (`nn`, 16-bit) | two operand bytes → 16-bit | 16-bit immediates (6502 has only 8-bit `#`) |
| `ExtendedAddress` (`(nn)`) | 16-bit absolute, both byte and word access | distinct from 6502 `Absolute` only in width |
| `IoPort` (`(n)`, `(C)`) | targets the I/O bus | Decision 2 |
| `RelativeJump` (`JR`, `DJNZ`) | PC + signed d | like 6502 `Relative` but unconditional/`B`-counted forms |
| `BitIndexed` (`CB`-prefixed `(HL)`/`(IX+d)`) | bit number + EA | a sub-operand (the bit index 0–7) the 6502 has no analog for |

**New micro-ops the Z80 needs** (extending `Op.cs`/`Spec.cs`/`s_microOpSignatures`/`FactoryArity`,
and the JIT's `JitOp` kind strings + `BlockCompiler` emit arms):

- **16-bit load/ALU:** `Load16`/`Store16` (register-pair and `(nn)` forms), `Add16` (`ADD HL,rr`),
  `Adc16`/`Sbc16` (`ED`-prefixed, and these *do* set flags, unlike `ADD HL,rr` which leaves S/Z),
  `Inc16`/`Dec16` (which **do not** set flags — a Z80 quirk the generator must not "helpfully" add).
- **Bit group (`CB`):** `BitTest(n)` (`BIT n,r` — sets Z from bit n, plus H, and the `X`/`Y`/`S`
  oddities), `BitSet(n)` (`SET`), `BitRes(n)` (`RES`) — each carries a **bit-index operand** the
  current `Op` records have no slot for (`JitOp` carries `RegA,RegB,FlagBit,BoolArg`,
  `OpcodeDescriptor.cs:32` — a bit index needs a new field or reuse of `FlagBit`).
- **Rotate/shift family:** the 6502 has `ASL/LSR/ROL/ROR`; the Z80 adds `RLC/RRC/RL/RR/SLA/SRA/SLL/
  SRL` plus the A-specific `RLCA/RRCA/RLA/RRA` (which set flags *differently* from the `CB` forms)
  and the BCD-rotate `RLD/RRD`. The existing `ShiftLeft`/`RotateLeft` ops (`Op.cs:26-29`) are a
  starting subset; the family roughly triples.
- **Block ops:** `LDIR`/`LDDR`/`CPIR`/`CPDR`/`INIR`/`OTIR` (and the single-step `LDI`/`CPI`/…).
  These are **self-repeating** — they decrement `BC` and re-execute until zero, modeled on silicon
  as an instruction that *does not advance PC* until `BC==0`. They stress the block model: a block
  op is a one-instruction loop.
- **`EX`/`EXX`:** `ExchangeDEHL`, `ExchangeAF` (`EX AF,AF'`), `Exx` (swap `BC/DE/HL` with primes),
  `ExchangeSPHL` (`EX (SP),HL` — a memory exchange).
- **Relative/conditional flow:** `RelativeJump` (`JR`), `DecrementJumpNotZero` (`DJNZ` — uses `B`),
  conditional `CALL cc,nn` / `RET cc` / `JP cc,nn` (the 6502 only branches conditionally; the Z80
  conditionally *calls* and *returns*), and the `RST n` restart vectors.
- **Misc:** `DAA` (needs `H` and `N`), `CPL`, `NEG`, `SCF`/`CCF`, `HALT` (a new halted CPU state),
  `DI`/`EI`, `IM 0/1/2` (interrupt-mode set — Decision 5), `IN`/`OUT` (Decision 2),
  `LD A,I`/`LD A,R` (which copy `R`/`I` and set flags including `P/V` from `IFF2`).

**What stresses the generator's class/mode matrix (vs. what the JIT has never seen):**

- *Generator class/mode matrix* — heavily stressed. `ClassifyOps`/`ValidateModeForClass` assume a
  fixed set of classes and 6502 mode-legality rules; the Z80 adds classes (16-bit ALU, bit ops,
  block ops, I/O, exchange) and breaks rules (conditional `CALL`/`RET`, `Inc16` setting no flags,
  the `(IX+d)` mode crossing almost every class). `RequiredIndexRegister` (`SpecParser.cs:582`) —
  "X-indexed needs a reg named X" — is meaningless for `IX+d`. **Expect the class/mode matrix to be
  substantially rebuilt, not extended.**
- *JIT emit loop* — has never seen: 16-bit arithmetic with 16-bit flags; the bit-index operand;
  block-op self-repeat (a JITted block op is a loop the compiler must emit, or fall back); the
  half-carry `H` computation (nibble-level); `DAA`; the I/O callout; `EX`/`EXX` (eight-field swaps).
  Many of these are reasonable **fallback** candidates initially (like BRK/RTI today,
  `OpcodeDescriptor.NeedsFallback`, `CpuEmitter.cs:1398`): emit them as interpreter-`Step` callouts
  first, prove correctness, then promote the hot ones.

**Recommendation.** Grow the vocabulary as above, but treat it as **two tiers**: (1) the
*interpreter* must implement every Z80 micro-op (correctness oracle — TomHarte gate); (2) the *JIT*
emits the straight-line, high-frequency ones (16-bit load/ALU, register-indirect, relative jumps)
and **falls back** for the rest (block ops, `DAA`, `EX (SP),HL`, I/O) initially. The fallback seam
already exists and is proven (ADC/SBC were fallbacks in M2-i, promoted to emitted in M2-ii —
`BlockCompiler.Decimal.cs`). Use the same staged approach.

**Genericity implication (high).** The micro-op vocabulary is *supposed* to be the CPU-agnostic IR
(research §2: "semantics in an IR → both tiers come from one spec"). The Z80 tests whether the IR
is genuinely a vocabulary or a 6502 transliteration. The bit-index operand alone proves a point:
`JitOp`'s fixed `(RegA,RegB,FlagBit,BoolArg)` shape (`OpcodeDescriptor.cs:32`) is 6502-sized. A
truly generic op needs an extensible operand model. **Whether the class/mode matrix survives Z80 or
gets rebuilt is the single best measure of how 6502-shaped the generator is — and it is the
strongest signal for the later optimization, because the optimizer reasons about op *classes*.**

### Decision 5 — Interrupt model

**The problem.** The 6502 model is hardwired-vector and lives entirely in the hand-written partial:
`TryServiceInterrupt` (`Mos6502Cpu.cs:78-100`) does the fixed 7-cycle sequence to vector
`$FFFA`/`$FFFE`; `InterruptPending` (`Mos6502Cpu.cs:69`) is `_nmiPending || (_irqLine && I clear)`;
the generated `Step` calls `TryServiceInterrupt()` before the opcode fetch (`CpuEmitter.cs:100`),
and the JIT samples `InterruptPending` at block boundaries / chain edges (`JittedCpu.cs:90`,
`BlockCompiler.cs:497`). The *seam* is already CPU-agnostic: the generated side declares
`private partial bool TryServiceInterrupt()` + `public partial bool InterruptPending`
(`CpuEmitter.cs:113, 1208`), and the per-CPU partial implements the policy. That is good design and
should survive.

The Z80 differs: **`IM 0`** (device supplies an opcode on the bus — usually `RST n`), **`IM 1`**
(fixed `RST 38h` → vector `0x0038`), **`IM 2`** (vectored: `I` register high byte + device-supplied
low byte form a pointer into a table), plus **NMI** (fixed `0x0066`, and it sets `IFF1←0`/saves
`IFF1→IFF2`), and the `IFF1`/`IFF2` interrupt-enable flip-flops (`DI`/`EI`, with `EI`'s
one-instruction delay — a documented quirk). `LD A,I`/`LD A,R` copy `IFF2` into `P/V`.

**Options.**

- **(A) Keep the hand-written-partial seam; the Z80 partial implements its own `IM 0/1/2` + NMI +
  IFF1/IFF2 logic.** The generated `Step`/JIT dispatcher are unchanged; only the partial differs.
  - *Pros:* zero `Core`/generator change — the seam was designed for exactly this; `InterruptPending`
    + `TryServiceInterrupt` are general enough to express any boundary-sampled policy.
  - *Cons:* the Z80's interrupt service can read a byte *from the device* (`IM 0`/`IM 2`), which
    means the I/O / interrupt-acknowledge bus cycle must be reachable from the partial — wiring,
    not a contract change. `EI`'s one-instruction delay needs the partial to track a "just enabled"
    latch (analogous to the 6502's documented-deviation CLI/SEI delay, design spec §6).

- **(B) Generalize interrupt servicing into the generated layer.** Make vectoring data-driven.
  - *Pros:* none compelling — interrupt policy is genuinely per-CPU and irregular.
  - *Cons:* over-engineering; the partial seam is the right altitude.

**Recommendation: (A).** The interrupt seam is the one place the framework is **already** generic
(it was built CPU-agnostic in M1 3b-ii). M3 confirms it: the Z80 implements `TryServiceInterrupt`/
`InterruptPending` in its partial with `IM 0/1/2` + NMI + `IFF1/IFF2`. The interrupt-acknowledge
read (for `IM 0`/`IM 2`) routes through the I/O bus wiring from Decision 2. The wired-OR
`IInterruptLine` (`InterruptLine.cs`) is reusable as-is for the Z80's `INT` line.

**Genericity implication (medium-low — and this is a *positive* finding).** This is the seam that
*should* survive Z80 unchanged, validating that the M1 interrupt-seam design was genuinely CPU-
agnostic and not 6502-shaped. The JIT's boundary-sampling of `InterruptPending` (`JittedCpu.cs:90`,
`BlockCompiler.cs:497`) is also already generic — it asks the CPU "is something pending?" without
knowing the policy. **If anything here needs a `Core` change, that is a finding; if nothing does,
that is the proof point** that the interrupt abstraction is real. (Watch item: the Z80's `HALT`
state — the CPU executes `NOP`s until an interrupt — needs the dispatcher/`Run` loop to not
busy-spin; that may touch `Run`, which is generated, `CpuEmitter.cs:116`.)

### Decision 6 — Extraction-as-acceptance-test

**The goal (design spec §9 item 10).** M3 doubles as the acceptance test of the datasheet-extraction
runbook: run the runbook against the Z80 manual to produce the opcode dataset "by extraction," not
by hand transcription. This is what `docs/user-guide/extraction-runbook.md` was built for, and the
runbook *explicitly* anticipates the Z80 (`extraction-runbook.md:188, 254`).

**The honest reality.** The extraction tooling is **6502-shaped in its vocabulary**, by the
runbook's own admission (`extraction-runbook.md:188`):

- `OpcodeDataset.ValidModes` is the 13 6502 modes (`OpcodeDataset.cs:37`); `ExpectedBytes` enforces
  6502 byte-count rules (`OpcodeDataset.cs:146`); the opcode format regex is `^0x[0-9A-Fa-f]{2}$` —
  **a single byte** (`OpcodeDataset.cs:45`), so it cannot even *represent* `ED B0`.
- `SemanticsMap.FactoryArity` is the 6502 factory list (`SemanticsMap.cs:38`).
- `SpecFileEmitter.SupportedModes` mirrors the 6502 `AddrMode` (`SpecFileEmitter.cs:41`).

So "extraction" for the Z80 means: **first extend the loaders** (new modes, the prefixed-opcode key
format from Decision 1, new factories from Decisions 3–4 — the runbook says this explicitly at
`:188`), **then** run the LLM extraction. The extraction eliminates *transcription* of ~1000 rows
and drafts semantics; it does **not** eliminate the hand work of the micro-op vocabulary and the
mode cycle-templates (the runbook is explicit: those "remain hand work by design," `:10`, `:305`).

**The verification ladder (the extraction-runbook.md:190-254 rungs), Z80-specific:**

1. **Loader validation** (`--validate-only`) — after extending the loaders to the Z80 vocabulary.
2. **Cross-source diff** (`--diff`) — extract the Z80 table from **two** independent documents
   (e.g. the Zilog Z80 CPU User Manual and a second datasheet/community opcode table) and diff.
   With ~1000 opcodes the diff is where most extraction errors surface — this rung does the heavy
   lifting.
3. **CPUGEN diagnostics** — the generator rejects DSL mistakes.
4. **End-to-end generator gate** (`ImporterEndToEndTests`-equivalent for Z80).
5. **SingleStepTests / TomHarte Z80 vectors** — the per-instruction, per-cycle truth. The runbook
   names this as the rung the Z80 "will exercise when the Z80 vectors become available"
   (`:254`). TomHarte's Z80 set covers the documented and undocumented opcodes (including the
   `X`/`Y` flag behavior) — it is the acceptance gate.

**Recommendation.** Run M3's dataset production through the runbook for real (it is the *test* of
the runbook), but **sequence the loader extension first** as its own PR (Decision 8). Use the
**TomHarte Z80 vectors as the primary acceptance gate**, with `ZEXALL`/`ZEXDOC` (the classic Z80
exerciser) as the integration-tier analog of the Klaus 6502 test. Budget the cross-source diff
generously: 1000+ rows × prefix structure is a real extraction load, and the diff tool is the
cheapest place to catch a wrong cycle count or a mis-assigned prefix.

**Genericity implication (medium).** The extraction pipeline is a microcosm of the whole framework:
it was built generic-*looking* but is 6502-vocabularied. M3 forces the loaders to become
data-driven about modes/factories/opcode-key-format. The payoff: a runbook that genuinely extends
to the 8086 next time, rather than one that "supports new CPUs" only on paper.

### Decision 7 — JIT genericity audit (the heart)

This is the decision the whole ADR exists to serve. **Enumerate every place the current JIT is
6502-shaped, and state how the Z80 tests/reshapes it.** Each row is a finding the later optimization
must not bake in.

| # | 6502-shaped assumption (file:line) | Why it is 6502-shaped | How Z80 reshapes it |
|---|---|---|---|
| J1 | **`BlockCompiler` is typed to `Mos6502Cpu`** — the field `Mos6502Cpu _cpu` (`BlockCompiler.cs:16`), the ctor (`:69`), and `BlockDelegate(Mos6502Cpu cpu, ...)` (`CompiledBlock.cs:49`). | The emitted IL and the delegate signature name the concrete 6502 type. | The compiler must be generic over `ICpuCore` (or a generated per-CPU state type). The `BlockDelegate` first parameter must be the CPU's generated type, resolved from the spec — not literally `Mos6502Cpu`. **Largest JIT change.** |
| J2 | **Six baked `FieldInfo`s `FA/FX/FY/FS/FP/FPC`** (`BlockCompiler.cs:37-42`) and `RegField` over indices 0–3 (`:454`). | Hardcodes the 6502 register *file* — names, count, and the `A=0…S=3` index map. | The Z80's ~14+ register fields (halves + pairs + alt set + I/R) must be resolved from the spec's `Registers` table and baked by name. Ties to Decision 3(ii). The register-hoisting optimization depends on this being data. |
| J3 | **`Mos6502Cpu.JitDescriptors[opcode]` — a literal 256-slot single-byte index** (`BlockCompiler.cs:81`). | Single-byte opcode (Decision 1). | Per-page tables + a generated decode walk; `Discover` advances PC by the decode function's total length, not one `d.Length` (`:84`). |
| J4 | **`LoadByteFromBus`/`EmitStoreByte` fastmem branch** keys on `addr >> 8` page and a `byte[]?[]` page table (`BlockCompiler.cs:277-316, 325-419`). | The fastmem split itself is sound and CPU-agnostic (it is the research §5 pattern). The 6502-shape is only that it serves **one** bus. | The Z80 reuses fastmem for the memory bus **unchanged** (good — proof the fastmem seam is generic), but **I/O must never enter it** (Decision 2). 16-bit memory accesses (`LD HL,(nn)`) do *two* byte accesses — composable from the existing byte helpers; no new fastmem shape, but the page-cross logic differs (the Z80 has no 6502-style page-cross penalty). |
| J5 | **Cycle templates: `EmitChargeOneCycle` charges per byte-access + the `BaseCycles`/page-cross model** (`BlockCompiler.cs:223`, `OpcodeDescriptor.BaseCycles`/`PageCrossPenalty`). The interpreter's per-cycle bus ordering (`ReadBus` charges then accesses) is mirrored in the JIT for "GT-F(a)" MMIO ordering (`BlockCompiler.cs:138-145`). | The 6502 is "one cycle = one bus transaction"; the page-cross +1 (`PageCrossPenalty`, `CpuEmitter.cs:1334`) is a 6502 timing quirk. | The Z80's timing is **not** one-cycle-per-bus-access — it has M-cycles and T-states, internal cycles, and per-instruction tables with no clean page-cross rule. The JIT's `BaseCycles` model must become "the descriptor carries the instruction's total T-state count" and the per-access charge model loosens. **The `PageCrossPenalty` field is 6502-specific and likely becomes one of several per-arch timing flags — a finding.** |
| J6 | **The interrupt check at block entry + chain edge** samples `InterruptPending` (`JittedCpu.cs:90`, `BlockCompiler.cs:497`). | This is already generic (Decision 5) — it asks the CPU, not the policy. | Survives — but `HALT` (a halted state) must be handled so a block of `HALT` doesn't spin; the dispatcher loop may need a "halted" fast path. |
| J7 | **Decimal arm (`BlockCompiler.Decimal.cs`)** emits the 6502 NMOS BCD `ADC`/`SBC` verbatim. | Pure 6502 — the BCD correction algorithm and the `D`-flag gate (`P & 0x08`). | The Z80 has **no `D` flag**; BCD is done by `DAA` *after* a binary add/sub, using `H`/`N`. So the decimal arm is **dead code for the Z80** and `DAA` is a *different* emitted arm. Confirms decimal handling must be per-CPU (a spec-declared capability), not a fixed JIT feature. |
| J8 | **SMC page assumptions**: the dirty-page bitmap + intra-block SMC guard (`BlockCompiler.cs:186-209`, `EmitContext.SpannedPages`) assume **256-byte pages** and that code runs from RAM. | 256-byte pages = the 6502 `AddressSpace.PageSize` (`AddressSpace.cs:10`); the guard logic is page-granular. | The page size is a `Core` constant, not a 6502 fact — the Z80 reuses it. SMC stays valid (Z80 software runs from RAM too). **This seam is genuinely generic; the Z80 confirms it.** The only nuance: the Z80's larger instructions (up to 4 bytes for `DDCB dd op`) span pages more often, exercising `PagesSpanned` (`BlockCompiler.cs:123`) harder. |
| J9 | **Block-ending classification** (`OpcodeDescriptor.EndsBlock`, `ClassifyForJit` `CpuEmitter.cs:1390`): Branch/Jump/Jsr/Rts/Flow/Undefined end a block; the chainable-vs-dynamic split (`BlockCompiler.Flow.cs`). | The 6502 control-flow set (JMP/JSR/RTS/branches/BRK/RTI). | The Z80 adds conditional `CALL`/`RET`/`JP`, `JR`/`DJNZ`, `RST n`, block ops (self-repeat), and `HALT`. The chainable-target analysis (static `JMP`/`JSR` target known at compile time — `BlockCompiler.Flow.cs:464,543`) must handle `RST n` (static vector — chainable), conditional calls (two static successors, like branches today), and `DJNZ` (a static backward target — chainable, and the hottest loop primitive on the Z80). **The optimizer's block/chain model is exercised much harder by the Z80** — this is where the optimization most needs to be arch-valid. |
| J10 | **`JitOp` operand shape `(RegA,RegB,FlagBit,BoolArg)`** (`OpcodeDescriptor.cs:32`) and `JitOpClass` enum (`:6`). | Sized for 6502 ops; no slot for a bit index (`BIT n`) or a 16-bit immediate. | Needs an extensible operand model (Decision 4). The `JitOpClass` enum grows (16-bit ALU, bit, block, I/O, exchange classes). |

**Recommendation.** Drive the Z80 through the JIT **after** it is correct in the interpreter, and
**record each row above as a measured finding** in the M3 closeout. Promote to emitted-IL only the
hot, straight-line Z80 ops; leave the irregular ones as fallbacks (the proven safety valve). The
output of this decision is the input to the post-M3 optimization milestone: a concrete list of "the
JIT assumed 6502 here, the Z80 forced it to be data."

> **OUTCOME (M3.5-3, 2026-06-15 — the J1–J10 table filled in).** The compiler is now generic
> (J1/J2/J3 RESOLVED: `BlockCompiler<TCpu>`/`JittedCpu<TCpu>` + the per-CPU `IJitTarget` seam; the
> JIT assembly no longer references a concrete CPU). J4/J6/J8 CONFIRMED GENERIC (the Z80 reused
> fastmem/SMC unchanged; the interrupt seam survived per Decision 5; the HALT fast path went live).
> J5 SURFACED (the all-fallback gate caught a Z80 op that would have emitted via the 6502 cycle
> model — fixed by forcing `NeedsFallback` for all structured-CPU descriptors). J7 CONFIRMED
> per-CPU. J9/J10 are the emit layer's concern (carried as data; not yet emitted). The Z80 runs
> through the generic JIT as ALL FALLBACKS with byte-identical tier parity. **The hot-op IL emission
> (the actual speed-up) is DEFERRED to M6 (the post-8086 cross-arch optimization), built once for all
> three ISAs.** Full record + the hot-op/fallback emit spec:
> `docs/superpowers/plans/2026-06-14-m3-z80-m35-3c-jit-genericity-findings.md`.

**Genericity implication (the whole point).** Today the JIT is, by construction (J1/J2/J3), a
*6502* JIT wearing a generic descriptor table. The descriptor *table* is CPU-agnostic; the
*compiler that consumes it* is not. M3 is what turns the compiler generic. **The single most
important outcome of M3 for the optimization goal is J1+J2+J3 — making the compiler generic over
the CPU type, the register file, and the decode structure — because every optimization (register
allocation, block linking, inlining) reasons about exactly those three things.** Optimizing before
M3 means optimizing the 6502 specifics; optimizing after M3 means optimizing the framework.

### Decision 8 — Proposed milestone decomposition (the M3 PR breakdown)

M3 is large (~1000 opcodes + framework reshaping). Decompose into reviewable, dependency-ordered
PRs. Each is one branch → PR (per the workflow). The cross-arch optimization is a **separate
post-M3 milestone**, not part of M3.

**M3.0 — This ADR.** (No code.) The decision record. *(done by this document)*

**M3.1 — DSL + generator: prefix decode + register/flag vocabulary (framework, no Z80 yet).**
Extend `AddrMode`/`Reg`/`Flag`/`RegisterRole`/`InstructionDef` and the generator mirror tables for:
the prefix/page model (Decision 1), the spec-declared register file replacing the closed `Reg` enum
(Decision 3(ii)), the richer flag micro-ops (Decision 3/4). Prove with **the 6502 spec
unchanged** — the existing generator snapshot tests + the `RegeneratedSpecTests` byte-equality pin
(`adding-a-cpu.md:78`) must stay green (zero-prefix is the special case). *This PR is the framework
genericity work; it is big and should be split if the register-file change and the prefix change
each prove large.* **Depends on:** M3.0.

**M3.2 — Bus + interrupt seam extensions.** The I/O-bus wiring (Decision 2 — CPU declares its
buses), the `HALT` handling in `Run` (Decision 5/J6). Small `Core` change, well-scoped. **Depends
on:** M3.1 (or parallel — touches different files).

**M3.3 — Extraction loaders + Z80 dataset (the runbook acceptance test).** Extend
`OpcodeDataset`/`SemanticsMap`/`SpecFileEmitter` to the Z80 vocabulary (Decision 6), then run the
runbook against two Z80 sources, diff, and commit the Z80 dataset + semantics map. Output: a
generator-clean `Z80Spec.cs`. **Depends on:** M3.1 (the DSL must accept the new modes/factories
before the emitter can emit them).

**M3.4 — Z80 interpreter (correctness oracle) + TomHarte gate.** The hand-written `Z80Cpu` partial
(reset, `R`-refresh, `IM 0/1/2` + NMI + IFF1/IFF2, I/O bus wiring, the block-op self-repeat, `DAA`,
`EX`/`EXX`), plus the interpreter bodies for every new micro-op (Decision 4). Gate on the
**TomHarte Z80 vectors** (per-cycle) + ZEXALL/ZEXDOC. **This is the biggest single chunk and will
likely split** (e.g. M3.4a base + `CB`/`ED`; M3.4b `DD`/`FD`/`DDCB`/`FDCB` + block ops + the
interrupt modes). **Depends on:** M3.2, M3.3.

**M3.5 — Z80 through the JIT + the genericity findings.** Make `BlockCompiler`/`JittedCpu`/
`BlockDelegate` generic over the CPU type and register file (J1/J2), drive the Z80 descriptor tables
through it (J3), emit the hot straight-line ops, fall back for the rest, and prove **tier parity**
(the differential fuzzer + the TomHarte sweep through the JIT, as M2-ii did for the 6502). Deliver
the **enumerated Decision-7 findings** as the milestone's headline artifact. **Depends on:** M3.4.

**M3.6 (optional) — Z80 host + monitor demo.** A `Breadboard Z80`-style machine (CP/M-ish or a
bare UART board) proving the composition seam for a second CPU, with the generated Z80 disassembler/
assembler driving the monitor (artifacts ④/⑤). **Depends on:** M3.4.

**POST-M3 — cross-arch JIT optimization (separate milestone).** *Only after* M3.5's findings exist.
Now register allocation, block chaining, and inlining can be optimized against **two** real register
files / decode structures / flag models, so the optimization is architecture-valid by construction.

**Sequence + dependency summary:**

```
M3.0 (ADR)
  └─> M3.1 (DSL/generator: prefix + reg/flag)  ──┬─> M3.3 (extraction + Z80 dataset)
        └─> M3.2 (bus/interrupt seams) ──────────┤
                                                 └─> M3.4 (Z80 interpreter + TomHarte)  [split likely]
                                                       └─> M3.5 (Z80 through JIT + findings)
                                                             └─> M3.6 (host/monitor demo, optional)
   ────────────────────────────────────────────────────────────> POST-M3 (cross-arch optimization)
```

**Which are big enough to split:** M3.1 (register-file change vs. prefix change), M3.4 (base+CB+ED
vs. DD/FD/DDCB/FDCB+block-ops+interrupt-modes). M3.2 and M3.3 are single-PR sized. **Estimated PR
count for M3: 6 base chunks, realistically 8 PRs with the two expected splits** (M3.1→2, M3.4→2).

---

## 3. What Z80 proves (and doesn't) about genericity

The owner's explicit requirement is that the post-M3 optimization be **valid across architectures**.
Honest read: **is one second architecture — the Z80, still an 8-bit micro — enough to validate
that?**

**What the Z80 genuinely proves:**

- The **decode model** stops being single-byte (Decision 1). Prefix decode is a real, different
  decode structure — the framework either becomes decode-driven or it doesn't. This is strong
  evidence.
- The **register file** stops being `A/X/Y/S` and the JIT's six baked fields (Decision 3, J1/J2).
  ~14+ registers, pairs, an alternate set, and special I/R force the register model to be data. The
  register-hoisting optimization will be exercised against a genuinely different file.
- The **flag model** stops being one `SetNZ` convention (Decision 3/4). Half-carry, parity/overflow
  dual-use, add/subtract, and undocumented X/Y prove the flag layer is a vocabulary, not a constant.
- The **I/O space** seam (Decision 2) goes from speculative to load-bearing.
- The **interrupt seam** is *confirmed* generic (Decision 5) — a positive proof, not a reshaping.
- The **fastmem + SMC + dirty-page seams** are *confirmed* generic (J4/J8) — the Z80 reuses them
  unchanged, which is itself evidence they were not 6502-shaped.

**What the Z80 does NOT prove (the honest gaps):**

- **The Z80 is still 8-bit, little-endian, byte-addressed, with a flat 16-bit address space and a
  256-byte-page bus.** It shares the 6502's *fundamental memory model*. So it does **not** exercise:
  - **Segmentation / non-flat addressing** — the 8086's `seg:offset` effective-address computation
    is a whole class of EA logic neither the 6502 nor the Z80 has. The JIT's "address is a `uint`
    you can page-index" assumption (`addr >> 8`, `EaLocal` is `uint`, `EmitContext.cs:24`) is
    untested against segmentation.
  - **32-bit (or wider) registers and data** — `RegisterDef.Bits` is capped at 16 (`RegisterDef.cs`,
    `SpecParser.cs:254`), and `AddressSpace` rejects >24 address bits with a "two-level table out of
    scope" note (`AddressSpace.cs:34`). The 68000's 32-bit registers and the fastmem table sizing
    are genuinely untested.
  - **Word-granular / big-endian decode** — the 68000 decodes a 16-bit word stream, big-endian. The
    decode walk from Decision 1 *can* generalize to it, but the Z80 doesn't *prove* it does.
  - **Misaligned-access / wider-than-byte bus transactions** — the `IPeripheral` contract carries
    `AccessWidth` already (research §3, the 68000 motivation), but the JIT bus arms are byte-only
    (`Read8`/`Write8`). The Z80's 16-bit memory ops decompose into two byte accesses, so even *it*
    does not exercise a true word bus transaction.

**Verdict.** **The Z80 is necessary and high-value but not sufficient for full confidence that the
optimization is architecture-independent.** It is the *right* second architecture because it is
maximally different from the 6502 *in the dimensions the JIT's front half cares about* — decode
structure, register file, flag model — while being similar enough in the memory model that M3 stays
tractable. It will flush out J1/J2/J3/J5/J7/J9/J10 (the decode/register/flag/cycle/block-model
assumptions), which are the assumptions most optimizations touch.

But the memory-model assumptions (J4's `uint` flat address, the >16-bit register cap, byte-only bus
transactions) will **survive the Z80 untested**. For *full* confidence the optimization is
arch-independent, a **more-different third architecture is needed** — and the natural choice is the
**8086** (segmentation + variable-length decode, TomHarte 8088 vectors exist) over the 68000
(32-bit + word bus — more work, but tests the widest set). My recommendation: **treat M3/Z80 as
"prove the front half (decode/register/flag/block model) is generic," and plan an M4 8086 as "prove
the memory/addressing half is generic" before declaring the optimization fully architecture-valid.**
Concretely: the post-M3 optimization can proceed on the *front-half* dimensions the Z80 validated
(register allocation, block chaining, decode-driven discovery) with confidence; it should be
**explicitly cautious** about baking in any memory-model assumption until the 8086 (or 68000) has
run. The design spec already lists 8086/68000 as the M4+ horizon (§9) — this ADR sharpens *why*:
they are not "more CPUs," they are the **second half of the genericity proof**.

---

### Decision (human checkpoint, 2026-06-13): a three-architecture ladder before optimization

The human accepted all four consequential recommendations (data-driven register file; generic
multi-byte-key decoder; partial Z80-through-JIT in M3; full cycle + undocumented-flag fidelity) and
**strengthened the genericity plan beyond this ADR's proposal**: rather than Z80 + one more, the
cross-architecture optimization is gated behind **three** diverse architectures, sequenced
**M3 Z80 → M4 68000 → M5 8086 → M6 optimization**. Rationale: the three together leave essentially
no genericity dimension untested — Z80 the front half (decode/register/flag/block), **68000** the
register-width + **big-endian** + word/long-bus + 24-bit-address half (the dimension this ADR's
verdict flagged as surviving Z80 untested), **8086** segmentation + variable-length decode. Both
68000 (24-bit address bus) and 8086 (20-bit physical) fit the current `addressBits ≤ 24` design, so
neither forces the deferred two-level page table; 68000's 32-bit registers + big-endian + word/long
transactions are the genuinely new pressure. Accepted trade-off: the JIT remains slower-than-Tier-0
until M6 — thoroughness over speed-now, consistent with the project's "the value is the abstractions"
thesis. Supersedes this section's "M4 8086 as the second half" framing: it is now M4 68000 **and**
M5 8086, in that order, *both* before the optimization.

---

## 4. Risks & open questions for the human

1. **Replace the `Reg` enum with spec-declared register names (Decision 3(ii))?** This is the
   highest-leverage genericity decision and the most invasive: it touches `Core` (`Reg.cs`), three
   generator mirror tables, and the JIT's baked-`FieldInfo` model (J2). The alternative (just widen
   the enum) is cheaper but leaves the JIT register model 6502-flavored. **My strong recommendation
   is to do the data-driven version, because the optimization depends on it — but it is a real cost
   and the owner should sign off.** *(Decision 3, J2.)*

2. **Decode model: nested prefix tables (A) vs. generic multi-byte key (B)?** I recommend (A) for
   the interpreter's hot path (dense arrays) with a generated decode walk that generalizes forward.
   But if the owner weights the 8086/68000 future heavily, (B)'s fully-general decoder might be
   worth the hot-path cost now. **This is a "pay once vs. pay per arch" call.** *(Decision 1.)*

3. **How far to push the Z80 *through the JIT* in M3?** The safe path emits only hot straight-line
   ops and falls back for block ops / `DAA` / `EX (SP),HL` / I/O. That is enough to surface the
   Decision-7 findings. Pushing for *full* Z80 JIT emission (block-op loops, `DAA` in IL) is more
   work and arguably belongs in the optimization milestone. **Where is the M3 line?** *(Decisions 4,
   7; M3.5.)*

4. **TomHarte Z80 vector availability and the undocumented flags.** The acceptance gate (Decision 6,
   J-row none) assumes the SingleStepTests Z80 set is available and that we commit to matching the
   **undocumented X/Y flags** (the vectors check them). Matching X/Y is more work than "documented
   behavior only." **Do we gate M3 on full undocumented-flag fidelity, or accept documented-only
   first (like the 6502's illegal-opcode deferral, design spec §11)?** *(Decision 3, 6.)*

5. **Cycle accuracy bar for the Z80 interpreter (M-cycles/T-states).** The 6502's "one micro-op =
   one cycle" made the interpreter cycle-true cheaply (design spec §6). The Z80's M-cycle/T-state
   model does not map as cleanly. **Do we hold the Z80 interpreter to per-T-state bus-trace fidelity
   (TomHarte's bar), or to instruction-cycle-count fidelity?** This sets how hard the interpreter
   bodies are to write. *(Decision 5, J5.)*

6. **The third architecture's identity and timing.** Section 3 argues the 8086 (or 68000) is needed
   for *full* optimization-genericity confidence. **Does the owner want to commit M4 = 8086 as the
   second half of the proof *now* (so the optimization can be designed knowing it is coming), or
   defer that decision until M3's findings land?** *(Section 3.)*

7. **Is `RegisterDef.Bits ≤ 16` and `addressBits ≤ 24` an acceptable ceiling through M3?** Both are
   fine for the Z80 but are the explicit walls the 68000 hits (`RegisterDef.cs`,
   `AddressSpace.cs:34`). **Confirm we are *not* pre-building 32-bit support in M3** (the research
   doc's scope-discipline risk: don't gold-plate toward a backend/arch we haven't reached). *(Section
   3.)*

8. **`HALT` and `Run`-loop changes.** The Z80 `HALT` state needs the generated `Run`
   (`CpuEmitter.cs:116`) / the JIT dispatcher to not busy-spin. This is a small generated-layer
   change — flag it as a likely *enumerated* `Core`/generator change against the §10 "zero changes"
   aspiration. *(Decision 5, J6.)*
