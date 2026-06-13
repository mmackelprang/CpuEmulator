# Intel 8086/8088 — Forward Architecture Research Brief (Milestone M5)

> **Editor's note (2026-06-13):** this brief was drafted reading the `feat/m3-register-file`
> working tree, which predates **ADR 0002 (address-space scaling)**. ADR 0002 now exists on `main`
> and confirms this brief's analysis: the 8086's 20-bit physical address fits the
> `addressBits ≤ 24` cap, and segmentation is CPU-internal math producing a flat physical address
> the bus already handles. Where the brief says "ADR 0002 does not exist," read "see ADR 0002 —
> consistent."
>
> **Status:** Forward research / scoping brief — **READ-ONLY analysis, no implementation.**
> **Date:** 2026-06-13
> **Milestone:** M5 (after M3/Z80, M4/68000; before M6/cross-arch JIT optimization)
> **Purpose:** (a) de-risk and front-load M5 by mapping the 8086 onto OUR seams; (b) feed the
> generalizations happening in M3 NOW — especially the M3.1b generic multi-byte-key decoder,
> which the 8086 is the **worst case** for.
> **Method:** mirrors the per-seam genericity-audit method of
> `docs/architecture/0001-z80-second-architecture.md` (the Z80 ADR). Every seam is examined as:
> *what the 8086 needs* → *how it stresses OUR current seam (with file:line citations)* →
> *what must grow*.
> **Basis docs:** the Z80 ADR (`docs/architecture/0001-z80-second-architecture.md`); the framework
> design spec (`docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`, §7 8086 note +
> §9 M5 line); the research doc (`docs/research/emulation-framework-research.md`, §7 8088 note); the
> M3.1a plan (`docs/superpowers/plans/2026-06-13-m3a-register-file.md`, which fixes what M3.1a does
> and explicitly **excludes** M3.1b/decode); the extraction runbook
> (`docs/user-guide/extraction-runbook.md`).

> **A note on the missing ADR 0002.** The task brief references
> `docs/architecture/0002-address-space-scaling.md`. **That file does not exist in the repo** (only
> `0001-z80-second-architecture.md` is present under `docs/architecture/`). Its substance — the
> `addressBits ≤ 24` cap and the deferred two-level page table — lives in `AddressSpace.cs:33-36`
> and in the Z80 ADR's §3/§4 (`0001-…:628-633, 715-718`) and the design spec's M4/M5 ladder lines
> (`…framework-design.md:273-279`). This brief grounds the segmentation analysis in those concrete
> sources instead. *(Flagged so the citation is honest.)*

---

## 0. The 8086 in one paragraph (what makes it different from 6502/Z80/68000)

The 8086 (and its 8-bit-bus sibling the 8088) is a 16-bit CISC with **four overlapping
general-purpose registers** — `AX/BX/CX/DX`, each addressable as a 16-bit whole *or* as two 8-bit
halves (`AH`/`AL`, `BH`/`BL`, …) that physically share storage — plus four pointer/index registers
(`SP/BP/SI/DI`), four **segment registers** (`CS/DS/SS/ES`), the instruction pointer `IP`, and a
16-bit `FLAGS` with 9 active bits (`O D I T S Z A P C`). Its headline feature is **segmentation**:
every memory reference computes a **20-bit physical address** as `(segment << 4) + offset`, with the
segment chosen implicitly per-operand (code uses `CS:IP`, stack uses `SS:SP`/`SS:BP`, data uses
`DS`, string destinations use `ES:DI`) and overridable by a one-byte **segment-override prefix**
(`CS:`/`DS:`/`SS:`/`ES:`). Instructions are **variable length, 1–6 bytes**, built from optional
**prefix bytes** (segment override, `LOCK`, `REP`/`REPNE`) + opcode + an optional **ModR/M byte**
(whose `mod`/`reg`/`r/m` fields encode register-vs-memory and one of ~24 16-bit effective-address
forms) + optional displacement (0/1/2 bytes) + optional immediate (0/1/2 bytes). There is **no SIB
byte** in 16-bit mode. The instruction set adds **string operations** (`MOVS/CMPS/SCAS/LODS/STOS`)
with the `REP` prefix, hardware `MUL/DIV` (8- and 16-bit), the BCD adjusts (`AAA/AAS/AAM/AAD` and
`DAA/DAS`), and an **interrupt-vector-table** model (256 vectors at physical `$00000`, `INT n`, the
divide-by-zero type-0 trap, NMI at type-2, maskable `INTR` gated by `IF`). Of the project's "simple
set," the design spec calls it **"hardest: segmentation, variable-length, prefixes"**
(`…framework-design.md:216-217`). It is validated by the **SingleStepTests 8088** hardware-captured
vector set.

The three dimensions this brief argues are *new beyond Z80 and 68000* — and therefore the highest
forward leverage — are: **(1) register aliasing** (AL/AH ⊂ AX — an overlap the data-driven register
file does not model); **(2) segmentation** (a CPU-internal address-math layer above the flat bus —
new, but containable); **(3) variable-length ModR/M decode** (the worst case for the M3.1b generic
decoder — and the single biggest M5 risk).

---

## 1. Register model — and the **aliasing** dimension (AL/AH ⊂ AX)

### 1.1 What the 8086 needs

| Group | Registers | Width | Notes |
|---|---|---|---|
| General (aliased) | `AX BX CX DX` | 16 | each = a high half + a low half: `AH/AL`, `BH/BL`, `CH/CL`, `DH/DL` |
| Pointer/index | `SP BP SI DI` | 16 | **no** byte halves; `SP`/`BP` default to the `SS` segment |
| Segment | `CS DS SS ES` | 16 | hold a paragraph base; feed the `seg<<4` math (§2) |
| Instruction ptr | `IP` | 16 | the ProgramCounter role; physical fetch = `CS:IP` |
| Flags | `FLAGS` | 16 | 9 active bits: arithmetic `O S Z A P C` + control `D I T` |

The `FLAGS` layout (bit positions, the ones the 8088 vectors check): `C`=0, `P`=2, `A`=4 (auxiliary
/ half-carry, used by the BCD adjusts), `Z`=6, `S`=7, `T`=8 (trap/single-step), `I`=9 (interrupt
enable), `D`=10 (direction, for string ops), `O`=11 (overflow). Bits 1/3/5/12-15 are undefined/“always
a fixed value” on the 8086 and the hardware-generated 8088 vectors **do pin those bits** to the
silicon's values (the same class of "undocumented flag bits" the Z80 ADR worried about with `X`/`Y`,
`0001-…:254, 700-701`).

### 1.2 How it stresses OUR seam

**Good news first — most of this is already paid for by M3.1a.** The closed `Reg` enum is *already
retired*: `Spec.cs:7-9` states register arguments are now register-NAME string literals validated
against the spec's own `Registers` table (CPUGEN008), and `SpecParser.cs:25-28` confirms "there is no
`s_regMembers` whitelist… the spec's declared register set IS the truth." So declaring
`AX/BX/.../ES/IP` is *data*, not a Core enum edit. `RegisterDef.Bits` already allows 16
(`RegisterDef.cs:6`, `SpecParser.cs:252-257`), and `RegisterRole` (`RegisterRole.cs:3-9`) covers
`ProgramCounter` (=`IP`), `StackPointer` (=`SP`), `Status` (=`FLAGS`), `General` (everything else).
The introspection contract (`ICpuCore.GetRegister/SetRegister`, `ICpuCore.cs:38-42`) zero-extends to
64 bits and truncates to natural width — fine for 16-bit registers.

**The genuinely new pressure: register *aliasing* — AL and AH are halves of AX.** Our register-file
model has **no overlap dimension**. Today each `RegisterDef` is an independent storage cell; the
generated state struct types it as `byte` or `ushort` (per the Z80 ADR's description of
`CpuEmitter.cs:39`), and the JIT bakes one `FieldInfo` per register *by name* (M3.1a Ground truth #4,
`m3a-register-file.md:48-51`). There is no concept of "`AL` is the low 8 bits of the `ushort` `AX`,"
so a write to `AL` must be visible through `AX` and vice-versa.

This is **the same shape of problem the Z80 already raised** — `B`/`C` are halves of `BC`
(`0001-…:249-251`, Decision 3 option (A): "8-bit halves are storage, pairs are a generated view").
**But the 8086 inverts the storage direction**, and that inversion matters:

- **Z80:** the *halves* are the natural storage (`B`, `C` are real registers); the *pair* `BC` is the
  synthesized 16-bit view. 8-bit is primary; 16-bit is the concatenation.
- **8086:** the *whole* `AX` is the natural 16-bit register; the *halves* `AH`/`AL` are the
  synthesized 8-bit views (`AL = AX & 0xFF`, `AH = AX >> 8`). 16-bit is primary; 8-bit is a sub-field.

So whichever way M3.4 (Z80) implements pair/half aliasing, **M5 needs the *other* direction too.**
The clean model is: a `RegisterDef` gains an optional **alias relationship** — "this 8-bit register
is the {low|high} half of that 16-bit register" (or, symmetrically for the Z80, "this 16-bit register
is the {hi,lo} pair of these two 8-bit registers"). The generator then synthesizes the views as
computed accessors over **one** backing field, so reads/writes alias correctly *by construction*
(no two-cell sync bug). The JIT's by-name `FieldInfo` map (M3.1a J2) must resolve `AL`/`AH` to the
*same* backing `AX` field plus a shift/mask, not to distinct fields — see §8.

### 1.3 What must grow

- **`RegisterDef`** gains an alias/overlap descriptor (a parent register + a half selector). This is a
  **new field on a Core spec type** — small, but it is a Core change, and it is the *same* change the
  Z80 needs (so M3/M4 should design it general enough to express both directions). Flag it as a
  shared M3↔M5 abstraction: **decide the aliasing model in M3.4, not improvised at M5.**
- **The generator** synthesizes half/whole accessors over a single backing cell and (for
  introspection) lists both `AX` and `AL`/`AH` in `RegisterNames`.
- **The flag layer** is out of M3.1a scope by design (`m3a-register-file.md:67-70`: "`Flag` stays a
  closed enum") — so the 8086's `FLAGS` (and the `A`/auxiliary half-carry, `O`/overflow with its own
  rules, `D`/`T` control bits) lands on whatever flag-vocabulary generalization M3.4 builds for the
  Z80's `S Z Y H X P/V N C`. The 8086 reuses the Z80's half-carry concept (`A` ≈ Z80 `H`) and
  parity (`P` ≈ Z80 `P/V`-as-parity) — so if M3.4 builds a composable flag micro-op family
  (`SetSZ`/`SetParity`/`SetHalfCarry`/`SetOverflow`, per `0001-…:301-308`), **the 8086 mostly reuses
  it**; the genuinely new flag micro-ops are `SetDirection`/`SetTrap` (control bits) and the
  carry/overflow rules for 16-bit-wide ALU + `MUL`/`DIV`.

**Verdict (register model):** aliasing is real new pressure but is *the same dimension the Z80
already forces, in the opposite direction*. **The forward input to M3 is: design the register-alias
relationship in M3.4 to be bidirectional (whole→halves AND halves→whole), so M5 inherits it.**

---

## 2. Segmentation — the headline new dimension

### 2.1 What the 8086 needs

Physical address = `(segment << 4) + offset`, masked to 20 bits (real-mode wrap at `$FFFFF`; on the
8088/8086 the address bus is 20 lines, so `$FFFF0 + $FFFF = $10FFEF` wraps to `$0FFEF`). The segment
is chosen implicitly:

| Memory reference | Default segment | Override allowed? |
|---|---|---|
| instruction fetch | `CS:IP` | no |
| stack push/pop, `SP`/`BP`-based EA | `SS` | `BP`-based EA: yes |
| general data (most EAs) | `DS` | yes (any of CS/DS/SS/ES) |
| string source (`SI`) | `DS` | yes |
| string dest (`DI`) | `ES` | **no** (always ES) |

Segment-override prefixes (`2E`=CS, `36`=SS, `3E`=DS, `26`=ES) force a non-default segment for the
*next* instruction's data EA. (Verified: 8086 defaults are DS for most, SS for BP-based, ES for
string dest. Sources at end.)

### 2.2 How it stresses OUR seam — the honest analysis

**Our bus is flat-addressed.** `IAddressSpace`/`AddressSpace` take a `uint` physical address
(`AddressSpace.cs:76 Read8(uint)`, `:104 Write8(uint)`), mask it to `AddressBits`
(`:78 address &= AddressMask`), and resolve a 256-byte page (`:79 _pages[address >> PageShift]`).
The JIT fastmem is built over the same flat page table (`Fastmem`, `TryGetDirectAccess` keyed on
`pageStart >> PageShift`, `AddressSpace.cs:131-145`) and the emitted IL computes `page = ea >> 8`
on a flat `uint` EA (`BlockCompiler.cs:288-290, 338-340`).

**Where does segment→physical translation live? The recommendation in the brief is correct, and it
holds:** segmentation is **CPU-internal address arithmetic that produces a flat 20-bit physical
address the bus already handles.** Nothing in `IAddressSpace` or the JIT fastmem needs to *know* about
segments. Concretely:

- A `Breadboard8086`-style machine builds **one** `AddressSpace(AddressSpaceKind.Program, addressBits:
  20)` — `20 ≤ 24`, so it fits the cap (`AddressSpace.cs:34`; design spec `…:276` "20-bit physical;
  also under the ≤24 wall"). The page table is `(1<<20)>>8 = 4096` pages of 256 bytes — trivially
  sized; **no two-level page table is triggered** (`AddressSpace.cs:33` note).
- The 8086's **effective-address micro-ops** compute `offset` (from ModR/M — see §4), then the CPU
  adds `(segReg << 4)` to form the 20-bit physical `uint`, and *that* `uint` is what flows into the
  existing `ReadBus`/`WriteBus` (the analogue of `Mos6502Cpu.ReadBus(uint)`, `Mos6502Cpu.cs:111`).
  The bus never sees a segment.
- **The JIT fastmem still works on physical addresses — verified against the code.** The emitted
  `LoadByteFromBus`/`EmitStoreByte` arms (`BlockCompiler.cs:277-419`) operate on `ctx.EaLocal`, a
  flat address already on the IL stack; they do `ea >> 8` to index the fastmem page table and
  `ea & 0xFF` for the page offset. **All segmentation does is change how `EaLocal` is *computed*
  upstream** (emit `(segReg << 4) + offset` instead of the 6502's `lo | (hi<<8)`); the page-table
  arm downstream is byte-identical. The fastmem split (research §5) is genuinely physical-address
  based and segmentation does not perturb it. **This is a positive genericity finding: segmentation
  is contained entirely in EA computation.**

**The subtleties that *do* bite:**

1. **20-bit EA, not 16-bit.** `BlockCompiler.cs` currently uses `Conv_U2` (truncate to 16 bits) when
   forming addresses (e.g. PC math `:259`) and the 6502/Z80 EA is a `ushort`. The 8086 physical EA is
   a **20-bit value living in a `uint`** (`EmitContext.EaLocal` is already `uint`, per the Z80 ADR
   `0001-…:624 "EaLocal is uint"`), and `seg<<4 + offset` can carry into bit 20 — the *physical*
   address must be masked to 20 bits at the bus (the existing `address &= AddressMask` at
   `AddressSpace.cs:78` does exactly this) **but the JIT's emitted page index `ea >> 8` must use the
   un-truncated `uint`**, not a `ushort`. So: the EA pipeline (interpreter + emitted IL) must be
   `uint`-wide end-to-end for the 8086 — the Z80 ADR already flags `EaLocal` as `uint`, so this is
   mostly paid; the risk is any `Conv_U2` that sneaks an address through 16 bits. **Audit item for
   M5: every address-forming `Conv_U2` in `BlockCompiler.*` must be width-correct (PC/IP is `ushort`,
   but the *physical EA* is 20-bit).**

2. **Offset wrap is 16-bit, but physical wrap is 20-bit — two different masks.** Within a segment,
   `offset` arithmetic wraps at 16 bits (e.g. `[BX+SI]` wraps the *offset*, staying in-segment);
   then `seg<<4 + offset` masks at 20 bits. These are two distinct masking points the EA micro-ops
   must model. Neither the 6502 nor the Z80 has this two-level wrap. It is **CPU-internal EA math**,
   so it does not touch the bus contract — but it is genuinely new EA-micro-op behavior to author.

3. **`SS`-relative stack vs `DS`-relative data — the implicit-segment selection is per-operand.**
   This is the part that does *not* live cleanly in a single addressing-mode template: the *same*
   ModR/M `r/m` form (`[BP+disp]`) uses `SS`, while `[BX+disp]` uses `DS`. So the segment selection
   is a function of the *base register in the EA*, plus the override-prefix state. This is more state
   than the 6502/Z80 EA templates carry — see §4.

### 2.3 What must grow

- **No `IAddressSpace`/`AddressSpace`/fastmem change** for segmentation itself (confirmed) —
  segmentation is above the bus. A 20-bit `Program` space is a config value, already supported.
- A **segment-relative EA computation layer** inside the 8086 CPU (interpreter + a matching JIT emit
  arm): `physical = ((segReg << 4) + offset) & 0xFFFFF`, with per-operand default-segment selection
  and override-prefix handling. This is new per-CPU code (analogous to the Z80's `(IX+d)` indexed-EA
  math, `0001-…:337`), **not** a Core/contract change.
- An **audit that no emitted address path truncates the physical EA to 16 bits** (the `Conv_U2`
  audit, item 1 above).

**Verdict (segmentation):** **the recommendation holds — segmentation is CPU-internal address math
producing a flat physical address the bus already handles; `IAddressSpace` and JIT fastmem need no
segmentation awareness, only a `uint`-wide (not `ushort`-truncated) physical EA pipeline.** The real
cost is authoring the EA micro-ops with the two-level wrap + per-operand segment selection — real
work, but localized to the 8086 partial/spec, not the framework. This is a *smaller* genericity risk
than the variable-length decode in §3.

---

## 3. Variable-length decode — the hardest decoder case (and the M3.1b crux)

> **This is the single most important section of the brief for M3 NOW.** Everything else the 8086
> needs is either pre-paid (I/O space, register-name data-drive) or localized to the 8086 partial
> (segmentation EA math). The decode strategy is the one place an M3.1b design choice can *paint M5
> into a corner*, because M3.1b is being built right now and M5 inherits whatever it ships.

### 3.1 What the 8086 needs

An 8086 instruction is a **left-to-right byte stream** with this grammar:

```
instruction := prefix* opcode modrm? displacement? immediate?
prefix      := segment-override (2E|36|3E|26) | LOCK (F0) | REP/REPNE (F3|F2)
opcode      := 1 byte   (a few 2-byte escapes exist on later x86; on the 8086 effectively 1)
modrm       := 1 byte = [mod:2 | reg:3 | rm:3]      (present for most, absent for some)
displacement:= 0, 1, or 2 bytes   — determined BY the modrm byte's mod+rm fields
immediate   := 0, 1, or 2 bytes   — determined BY the opcode (and the operand-size it implies)
```

Total length: **1 to 6 bytes**. Crucially, **the length is not a property of the first byte** — it is
*computed by consuming the stream*: the opcode says "I have a ModR/M"; the ModR/M's `mod`+`rm` say
"there is a 0/1/2-byte displacement"; the opcode says "there is a 0/1/2-byte immediate." (The real
8086 has *no* architected length limit — it just keeps consuming; 6 bytes is the practical max for
real instructions and matches the 8088's 4-byte / 8086's 6-byte prefetch queue. Source at end.)

This is **categorically harder than the Z80's prefix decode.** The Z80 ADR's Decision 1
(`0001-…:98-179`) handles a *finite set of known prefix bytes* (`CB`/`ED`/`DD`/`FD`) that switch to a
known table; the worst Z80 oddity is `DDCB dd op` (a displacement *between* prefix and opcode). But on
the Z80 the *opcode still determines the length* once you know the table. **On the 8086 the length is
data-dependent on a byte (ModR/M) that is itself in the middle of the instruction.** You cannot index
a table by "the first byte" *or* by "the prefix + opcode" to get a length — you must *parse the
ModR/M* to know how many displacement bytes follow.

### 3.2 How it stresses OUR seam — brutally honest

**Today's decode is single-byte, end to end** (this is the 6502 shape the Z80 ADR's Decision 1 set
out to break, and which M3.1a explicitly left untouched, `m3a-register-file.md:61-64`):

- `InstructionDef(byte Opcode, …)` — **one byte** (`InstructionDef.cs:7`); the parser rejects opcodes
  outside `0x00..0xFF` (`SpecParser.cs:352-358`).
- `OpcodeDescriptor.Length` is a **fixed `int` per opcode** ("1-3, the InstructionLength value;
  discovery advances PC by this", `OpcodeDescriptor.cs:41`).
- The JIT's `Discover` reads **one byte** and indexes a flat 256-slot table by it, advancing PC by the
  static `d.Length`: `byte opcode = _bus.Read8(pc); OpcodeDescriptor d = Mos6502Cpu.JitDescriptors[opcode];
  … pc += d.Length;` (`BlockCompiler.cs:80-84`).
- `PagesSpanned` walks `d.Length` bytes per instruction (`BlockCompiler.cs:128`).
- The importer enforces a **single-byte** opcode key (`OpcodeFormat = ^0x[0-9A-Fa-f]{2}$`,
  `OpcodeDataset.cs:45-46`) and **derives byte-count from the mode alone** (`ExpectedBytes(mode)`,
  `OpcodeDataset.cs:146-153`) — a 6502 assumption that *the mode fixes the length*. **On the 8086 the
  mode does not fix the length** (the same `reg,r/m` mode is 2 bytes with `mod=11`, 4 bytes with
  `mod=10`).

**The central question: does the M3.1b generic multi-byte-key decoder generalize to ModR/M-driven
variable length, or does the 8086 need a fundamentally different decode strategy?**

The honest answer depends on **which of the Z80 ADR's two decoder options M3.1b actually picks**, and
the project has *already committed to the more general one*:

- The design spec's M3 line (`…framework-design.md:265`) and the human checkpoint
  (`0001-…:658-673`) lock in **"a generic multi-byte-key decoder (retire the `[256]`/`switch(opcode)`
  shape)"** — i.e. Decision 1 **option (B)**, the decode *state machine* that "consumes bytes until it
  has resolved a full opcode," explicitly chosen because it "extends to the **8086's variable-length
  prefixed encoding**" (`0001-…:134-143`).
- M3.1b is **not yet implemented** (`m3a-register-file.md:61-66` — "SEPARATE later chunk… the opcode
  stays a single byte… M3.1b is the decode dimension"). So the design is *open right now*.

**The critical distinction the M3.1b design MUST internalize:** option (B) as described in the Z80
ADR produces a **canonical multi-byte *key*** that *excludes data bytes* (the ADR is explicit: "the
displacement byte (`DDCB dd op`) is *data*, not part of the opcode key — the key must exclude it while
decode still consumes it positionally", `0001-…:140-143`). That framing is **necessary but not
sufficient for the 8086.** For the Z80, decode is "consume known prefixes, then the opcode byte is the
last byte of the key, then *length is known from a table*." For the 8086, **decode and
length-determination are inseparable** — you cannot know the length until you have parsed the ModR/M's
`mod`/`rm` (to count displacement bytes) *and* know the opcode's immediate width. The key (opcode +
the ModR/M's `reg`/`mod`/`rm` semantics that select the operation/operands) is decoupled from the
*length* (which needs the same ModR/M byte plus the opcode's immediate rule).

So the 8086 needs the decoder to be modeled not as "read N prefix/opcode bytes → look up a fixed-length
descriptor," but as a **per-byte consumption state machine** whose states are:

```
[prefixes] → [opcode] → (if opcode has ModR/M) [modrm] → [disp: 0/1/2 per mod+rm]
                                                        → [imm:  0/1/2 per opcode]
```

…and whose **output is `(operation, operands, total-consumed-length)`** where the length is *computed
during the walk*, not read from a slot. This is exactly the "decode state machine / per-byte
consumption" the task brief names as the alternative.

**Does option (B) survive? Yes — IF it is designed as a per-byte-consumption walk whose length is an
*output*, not a table field.** The Z80 ADR's option (B) ("a generic front-end decoder consumes bytes
until it has resolved a full opcode") is *already the right shape* — the danger is implementing it for
the Z80 as a thin veneer over a still-fixed-length table (e.g. "the key is up to 4 bytes; the
descriptor still carries a static `Length`"). **If M3.1b ships a decoder where `Length` is a static
field on the descriptor (as `OpcodeDescriptor.Length` is today, `OpcodeDescriptor.cs:41`), the 8086
breaks it and M5 pays the decode cost a second time** — precisely the "pay once vs. pay per arch"
trap the Z80 ADR's open-question 2 warns about (`0001-…:688-689`).

### 3.3 What must grow

- **`InstructionDef`** must stop carrying a single `byte Opcode` and a mode-implied fixed length. For
  the 8086 a "row" is keyed by `(opcode, modrm.reg-extension?)` — because many 8086 opcodes are
  **opcode-group** encodings where the ModR/M `reg` field is an *opcode extension* (e.g. `0x80`/`0x81`
  groups select ADD/OR/ADC/SBB/AND/SUB/XOR/CMP by `reg`; `0xFE`/`0xFF` select INC/DEC/CALL/JMP/PUSH;
  the shift group `0xD0-0xD3`). **The decode key is `opcode<<3 | reg` for grouped opcodes** — a
  16-bit-ish key, exactly the multi-byte key option (B) anticipates, but extended to "key includes a
  *sub-field of the ModR/M byte*."
- **The descriptor must carry a *length rule*, not a length** — a small function/enum of "ModR/M
  present?", "displacement-size = f(mod,rm)", "immediate-size = f(opcode, operand-width)". The
  `Discover` loop (`BlockCompiler.cs:75-87`) must call the **decoder's computed total length**, not
  read `d.Length`. The Z80 ADR already says discovery must "advance PC by the **decode function's**
  total length, not a single `d.Length`" (`0001-…:167-168, 507`) — so this is *already on the M3
  radar*; M5's contribution is proving the "decode function" is rich enough to compute length from a
  mid-stream byte.
- **The importer schema** (`OpcodeDataset.cs`) must drop the single-byte `OpcodeFormat` regex and the
  `ExpectedBytes(mode)` length-from-mode rule; for the 8086, "bytes" is a *minimum* (prefixes+opcode+
  modrm) plus a computed disp/imm. This is the loader-extension-first work the runbook already says is
  required per family (`extraction-runbook.md:188`).
- **`PagesSpanned`** (`BlockCompiler.cs:123-131`) must walk the *decoded* length; a 6-byte instruction
  can span up to 2 pages, exercising the SMC/dirty-page span logic harder (the same observation the
  Z80 ADR makes about its 4-byte `DDCB` forms, `0001-…:512`).

**Verdict (decode) — the headline answer for M3:** **the M3.1b decoder design MUST anticipate
variable-length ModR/M decode NOW — specifically by modeling decode as a per-byte-consumption walk
whose total length is a computed OUTPUT, not a static descriptor field.** It does **not** need to
*implement* ModR/M in M3.1b (that is M5's extraction + EA-micro-op work), but it must not bake the
fixed-`Length`-per-key assumption that today's `OpcodeDescriptor.Length` (`OpcodeDescriptor.cs:41`)
and `Discover`'s `pc += d.Length` (`BlockCompiler.cs:84`) embody. If M3.1b ships option (B) as "a
multi-byte key into a table of fixed-length descriptors," **that is a corner M5 will have to demolish.**
If M3.1b ships option (B) as "a decode walk that returns `(operation, operands, length)`," the 8086
slots in as "a longer walk with a ModR/M state and data-dependent disp/imm counting." This is the
project's stated intent (`0001-…:134-143`); the brief's job is to make sure M3.1b's *implementation*
honors the *length-is-an-output* property, not just the *multi-byte-key* property. **(Highest-leverage
forward input — repeated in §10.)**

---

## 4. Addressing modes via ModR/M

### 4.1 What the 8086 needs

The ModR/M byte = `mod:2 | reg:3 | r/m:3`. `mod` selects the overall form; `r/m` selects a register
or one of the memory base+index combinations; `reg` is either a second register operand or an
opcode-group extension (§3). The 16-bit memory forms (`mod ≠ 11`):

| r/m | EA base (offset) | default segment |
|---|---|---|
| 000 | `[BX+SI]` | DS |
| 001 | `[BX+DI]` | DS |
| 010 | `[BP+SI]` | SS |
| 011 | `[BP+DI]` | SS |
| 100 | `[SI]` | DS |
| 101 | `[DI]` | DS |
| 110 | `[BP]` (or, if `mod=00`, a direct 16-bit `[disp16]`) | SS (BP) / DS (disp16) |
| 111 | `[BX]` | DS |

`mod` adds displacement: `00` = no disp (except `r/m=110` → disp16 direct); `01` = `disp8`
(sign-extended); `10` = `disp16`; `11` = `r/m` is a register, not memory (no EA). That is the
**5-bit (mod+rm) → 24-form** space the search confirmed, with **no SIB** in 16-bit mode (sources at
end).

### 4.2 How it stresses OUR seam

Our `AddrMode` (`AddrMode.cs:6-12`) is a **closed enum of 13 explicitly-enumerated 6502 modes**, and
the generator's class/mode matrix (`SpecParser.cs:127-143, 580-644`) is **per-opcode mode legality
with 6502 rules baked in** (e.g. `RequiredIndexRegister` "X-indexed needs a reg named X",
`SpecParser.cs:580-585`; `s_loadAluModes`/`s_storeModes`/`s_rmwModes`). The JIT mirrors this as
`JitMode` (`OpcodeDescriptor.cs:19-25`).

The 8086 breaks this model at a deeper level than the Z80 did:

- **The 8086's addressing is *encoding-driven, not opcode-enumerated*.** On the 6502 (and largely the
  Z80), the *opcode* names the mode (`0xBD` = `LDA abs,X`). On the 8086, the opcode says "I take a
  ModR/M operand" and the **ModR/M byte itself selects the addressing form at runtime**. So a single
  spec row (`MOV r/m16, r16`, opcode `0x89`) covers register-target, `[BX+SI]`, `[BP+disp8]`, `[disp16]`
  direct, etc. — **one opcode, 24 EA forms, chosen by a byte the decoder reads.** The closed
  `AddrMode` enum cannot enumerate this as 24 opcode rows; the natural model is **a single
  `ModRM` addressing mode whose concrete EA is computed by the EA micro-op from the decoded `mod`/`rm`.**
- **`RequiredIndexRegister` (`SpecParser.cs:580-585`) is meaningless** for ModR/M — the "index" is
  `SI`/`DI` selected by `rm` bits, not a mode-name suffix. The whole `Required*` / per-class mode-set
  machinery (a 6502-ism the Z80 ADR already predicted would be "substantially rebuilt, not extended",
  `0001-…:371-378`) does not apply; the 8086 wants **"the operation takes a ModR/M operand of width
  8 or 16"** as the mode, with the EA resolution being a decoder/micro-op concern.
- **Per-operand segment selection** (§2.2 item 3) is encoded *in the EA form*: `[BP…]` ⇒ `SS`, others
  ⇒ `DS`, modified by an override prefix. The EA micro-op must carry the base-register→segment mapping
  + the override-prefix state. This is more EA state than any current mode template (the 6502/Z80
  templates are pure offset math; none selects a segment).

### 4.3 What must grow

- **`AddrMode`/`JitMode`** gain (at least) a `ModRM8`/`ModRM16` family (operand is a ModR/M-encoded
  reg-or-memory of the given width) plus the fixed implicit forms the 8086 also has (`Immediate8/16`,
  `accumulator-direct moffs`, `relative8/16` for jumps, `far ptr16:16` for `CALL FAR`/`JMP FAR`). The
  EA *forms* (`[BX+SI]`, …) are **not** enumerated as modes — they are the decoder's job, resolved by
  an EA micro-op from the decoded ModR/M.
- **The class/mode matrix** (`SpecParser.cs:399-644`) is **rebuilt** for the 8086 (the Z80 already
  forces a substantial rebuild; the 8086 pushes it from "enumerate legal modes per class" to "this op
  takes a ModR/M operand"). This is generator work, gated by the new mode vocabulary.
- **A ModR/M EA micro-op** in the 8086 partial + a matching JIT emit arm: decode `mod`/`rm` → compute
  16-bit offset (with the two-level wrap, §2) → select default segment from the base register (or the
  override prefix) → `physical = (seg<<4)+offset`. Localized to the 8086, not the framework.

**Verdict (addressing):** the ModR/M model is the reason the **decoder (§3) and the addressing model
are the same problem** for the 8086 — the EA "mode" is data the decoder reads, not an opcode label. The
class/mode matrix rebuild is already on the M3 path for the Z80; the 8086 deepens it from
"opcode-enumerated modes" to "encoding-driven operands." This is real generator work but, like
segmentation, it does **not** touch the bus/JIT contracts — it touches the spec DSL + generator + the
per-CPU EA micro-ops.

---

## 5. Instruction set — operation groups and new micro-ops

### 5.1 The operation groups (structural, provisional — see §9)

- **Data movement:** `MOV` (the most-encoded mnemonic — reg/mem/imm/seg/accumulator-direct forms),
  `PUSH`/`POP` (reg, mem, segment-reg), `XCHG`, `XLAT`, `LEA` (load EA — computes offset without a
  memory access), `LDS`/`LES` (load pointer: offset + segment in one op), `LAHF`/`SAHF` (AH ↔ flags).
- **ALU with direction/width bits in the opcode:** `ADD/OR/ADC/SBB/AND/SUB/XOR/CMP` each occupy a
  block where **bit 1 of the opcode = direction (`d`: reg←r/m vs r/m←reg)** and **bit 0 = width (`w`:
  8 vs 16-bit)**. This *encoding-embeds operands into the opcode bits* — a dimension the 6502/Z80
  opcode tables do not have (a 6502 opcode is atomic; an 8086 ALU opcode is `0b000000dw` for ADD).
  The decoder/generator must understand the `d`/`w` bits as operand selectors. Plus the
  immediate-form group (`0x80-0x83`, ModR/M `reg` = which ALU op) and accumulator-immediate short
  forms.
- **Shifts/rotates:** `SHL/SHR/SAR/ROL/ROR/RCL/RCR` (group `0xD0-0xD3`, ModR/M `reg` selects; count =
  1 or `CL`).
- **String ops + REP:** `MOVS/CMPS/SCAS/LODS/STOS` (8/16-bit), each implicitly using `SI`(DS:) and/or
  `DI`(ES:), auto-incrementing/decrementing by the **direction flag `D`**, repeated by the `REP`/
  `REPE`/`REPNE` prefix counting down `CX`. These are **self-repeating like the Z80's `LDIR`/`CPIR`
  block ops** (`0001-…:358-363`) — a one-instruction loop that does not advance IP until `CX==0`
  (or the `Z`-flag condition for `REPE`/`REPNE` on `CMPS`/`SCAS`).
- **Control transfer:** `JMP`/`CALL` (near rel16, near indirect via ModR/M, far ptr16:16, far
  indirect), `RET`/`RETF` (near/far, optional immediate stack-adjust), the conditional jumps
  `Jcc` (16 condition codes, rel8), `LOOP`/`LOOPE`/`LOOPNE` (count `CX`, like the Z80 `DJNZ`),
  `JCXZ`.
- **Flags:** `CLC/STC/CMC`, `CLD/STD` (direction), `CLI/STI` (interrupt enable), `PUSHF/POPF`.
- **Interrupts:** `INT n`, `INT 3` (the 1-byte breakpoint `0xCC`), `INTO` (overflow trap), `IRET`,
  the implicit type-0 (divide error) and type-1 (single-step via `T`). See §6.
- **Arithmetic helpers:** `MUL`/`IMUL` (8-bit → AX, 16-bit → DX:AX), `DIV`/`IDIV` (with the type-0
  divide-error trap on overflow/÷0), `INC`/`DEC` (reg short forms `0x40-0x4F`, and the group form),
  `NEG`, `NOT`, `CBW`/`CWD` (sign-extend AL→AX, AX→DX:AX).
- **BCD adjusts:** `DAA`/`DAS` (decimal adjust after add/sub, use the `A`/auxiliary flag — directly
  analogous to the Z80 `DAA`, `0001-…:367`), `AAA`/`AAS`/`AAM`/`AAD` (ASCII adjusts; `AAM`/`AAD`
  carry an immediate base byte).
- **Misc/CPU control:** `NOP` (= `XCHG AX,AX`), `HLT` (a **halted state**, like the Z80 `HALT`,
  `0001-…:445-447`), `WAIT`, `LOCK` (prefix), `ESC` (the FPU-coprocessor escape — the 8088 vectors
  mark these `fpu` status and they are out of scope for M5).

### 5.2 New micro-ops vs 6502/Z80 (what the IR vocabulary must gain)

The current micro-op vocabulary is the 6502's (`Op.cs:8-46`, `Spec.cs:15-52`,
`s_microOpSignatures` `SpecParser.cs:38-75`). The Z80 (M3.4) already adds 16-bit load/ALU, bit ops,
the rotate/shift family, block ops, `EX`/`EXX`, relative/conditional flow, `DAA`, `IN`/`OUT`
(`0001-…:344-369`). **The 8086 reuses most of the Z80's additions and adds:**

- **`d`/`w`-bit operand resolution** — not a micro-op per se, but a *decoder/operand* concept the
  IR's operand model must express (the operand comes from a ModR/M `reg`/`r/m` selected by opcode
  bits). The Z80 ADR already notes `JitOp`'s fixed `(RegA,RegB,FlagBit,BoolArg)` shape
  (`OpcodeDescriptor.cs:32`) is "6502-sized" and needs "an extensible operand model"
  (`0001-…:396-397, 514`). **The 8086 makes this acute:** an operand can be "the register named by
  ModR/M `reg`," "the EA computed from ModR/M `r/m`," or "an immediate of width `w`" — operands are
  *decoded*, not baked. The extensible-operand model the Z80 ADR calls for is a **hard prerequisite**
  for the 8086, not a nicety.
- **String micro-ops + REP loop** — `LoadString`/`StoreString`/`CompareString`/`ScanString` that
  use `SI`(DS)/`DI`(ES), adjust by `±1`/`±2` per the `D` flag, and a `Rep`/`RepWhileZ`/`RepWhileNZ`
  loop wrapper that counts `CX` and (for CMPS/SCAS) tests `Z`. Same self-repeat shape as the Z80
  block ops (`0001-…:358-363`); reuse that machinery.
- **`MUL`/`DIV` wide-result micro-ops** — produce a 16-bit (or 32-bit `DX:AX`) result from an 8/16-bit
  multiply, and the **divide-error trap** path (a *micro-op that can raise a guest interrupt* — new
  control flow inside an instruction; see §6). 6502/Z80 have no hardware multiply/divide.
- **Segment-relative EA micro-op** (§2/§4) — the `seg<<4 + offset` computation with per-operand
  segment selection.
- **`INT n`/`IRET`/`INTO` micro-ops** — push FLAGS+CS+IP, clear `I`/`T`, vector through the IVT (§6).
- **`LDS`/`LES`** — load offset + segment in one op (two memory reads, writes a GP register + a
  segment register).
- **`LEA`** — compute an EA *offset* and store it without touching memory (an EA-as-value op the
  6502/Z80 lack).
- **Direction/trap flag ops** — `CLD/STD`, and the `T` single-step machinery (§6).

**Genericity implication (high).** The 8086 is the strongest test of whether the micro-op IR is a
genuine *vocabulary* or a transliteration. The decisive item is the **operand model**: the 6502 and
Z80 mostly name operands statically (a register, a fixed flag); the 8086 *decodes* operands from the
ModR/M byte and opcode bits. The Z80 ADR already flags `JitOp`'s fixed operand shape as needing to
grow (`0001-…:396-397, 514`); **the 8086 confirms that an extensible/decoded operand model is
mandatory, and that it is the same change the Z80's bit-index operand began.** This is forward input
to M3.4/M3.5: design the operand model to carry *decoded* operands, not just baked indices/names.

---

## 6. Interrupts — generalizing across THREE now-different models

### 6.1 What the 8086 needs

- **Interrupt vector table (IVT) at physical `$00000`:** 256 four-byte vectors (offset word + segment
  word). `INT n` (n=0..255) pushes `FLAGS`, then `CS`, then `IP`; clears `IF` and `TF`; loads
  `IP`/`CS` from `IVT[n]`. `IRET` reverses it (pops IP, CS, FLAGS — restoring `IF`/`TF`).
- **Predefined types:** type-0 = divide error (raised by `DIV`/`IDIV` on overflow/÷0 — *internal*,
  raised mid-instruction); type-1 = single-step (taken after each instruction when `TF` set); type-2
  = NMI (the non-maskable line); type-3 = breakpoint (`INT 3`, the 1-byte `0xCC`); type-4 = `INTO`
  overflow trap.
- **Maskable `INTR`:** the external interrupt line, gated by `IF` (`CLI`/`STI`). On real hardware the
  device supplies the vector number on the bus during the interrupt-acknowledge cycle (like the Z80's
  `IM 2` / `IM 0`). For a simple board this is often wired to a fixed vector or an 8259 PIC (a phase-A
  device, `…framework-design.md:280-281`).

### 6.2 How it stresses OUR seam — and the three-model generalization

**The interrupt seam is the one place the framework was deliberately built CPU-agnostic in M1**, and
the Z80 ADR confirmed it survives the Z80 *unchanged* (Decision 5, `0001-…:402-447`, a "positive
finding"). The seam is: the generated `Step` calls `private partial bool TryServiceInterrupt()` before
the opcode fetch, and `public partial bool InterruptPending { get; }` is the boundary-sampling
predicate; the per-CPU partial implements the *policy*. The 6502 implements it concretely in
`Mos6502Cpu.cs:69 (InterruptPending)` and `:78-100 (TryServiceInterrupt)`.

The three models the seam now spans:

| CPU | Vectoring model | Enable/mask | Special |
|---|---|---|---|
| 6502 | **fixed vectors** ($FFFA NMI / $FFFE IRQ) | `I` flag (IRQ only) | NMI edge-latched |
| Z80 | **IM 0/1/2** (bus opcode / fixed $0038 / `I`-reg table) | `IFF1`/`IFF2`, `EI` delay | NMI fixed $0066 |
| 8086 | **vector table** (256 entries at $0, `INT n`) | `IF` flag (`INTR` only) | type-0 divide, type-1 trap (`TF`), `INT n` software |

The 8086 fits the *existing partial seam* cleanly for the **boundary-sampled** parts (`INTR`/`NMI`):
the 8086 partial implements `InterruptPending` (`= NMI-pending || (INTR-line && IF)`) and
`TryServiceInterrupt` (push FLAGS/CS/IP, vector through `IVT[n]`), exactly as the 6502 does — **no
Core/generator change for these.** The interrupt-acknowledge vector read (for `INTR`) routes through
the bus, like the Z80's `IM 2` (`0001-…:426-428`).

**But the 8086 adds two interrupt sources the *boundary-before-fetch* seam does not cover:**

1. **`INT n` / `INTO` / `INT 3` are *instructions*** — they raise an interrupt *as their semantics*,
   not at a boundary. These are just micro-ops that do the push+vector sequence (a `Software­Interrupt`
   micro-op), so they live in the instruction body, not `TryServiceInterrupt`. Fine — new micro-ops
   (§5), not a seam change.
2. **The type-0 divide error is raised *mid-instruction*** by `DIV`/`IDIV`, and **type-1 single-step
   is taken *after* each instruction when `TF` is set.** The divide trap is a micro-op raising an
   interrupt inline (like `INT n`). The single-step trap is the genuinely new shape: it is a
   **post-instruction** service (after the instruction that ran with `TF` set), not a
   pre-fetch-boundary service. The current seam samples *before* the fetch (`Step` calls
   `TryServiceInterrupt` then fetches, mirrored at `CpuEmitter.cs` Step). The `TF` trap needs a
   *post-instruction* hook. This is a small generated-`Step`/partial extension — analogous to (and no
   bigger than) the Z80's `HALT` / `EI`-delay latches the Z80 ADR already flags as partial-level state
   (`0001-…:428, 445-447`). **Likely an enumerated, justified generated-layer change, not a Core
   contract break.**

**The JIT side** samples `InterruptPending` at block boundaries / chain edges (`BlockCompiler.cs:496-499`,
the same generic seam the Z80 ADR confirmed, `0001-…:510`). The 8086 reuses this for `INTR`/`NMI`.
The `TF` single-step trap, if emulated in the JIT at all, forces **block-boundary = instruction
boundary** when `TF` is set (a single-instruction block) — but single-stepping is inherently a
debug/trap path and is a reasonable **fallback-to-interpreter** case (the proven safety valve,
`BlockCompiler.cs:541-563`), so the JIT need not emit it.

### 6.3 What must grow

- **No Core change** for `INTR`/`NMI`/`INT n` — they fit the existing `TryServiceInterrupt`/
  `InterruptPending` partial + new micro-ops. This **re-confirms the interrupt-seam abstraction
  across a *third* model**, strengthening the Z80 ADR's positive finding.
- **A post-instruction trap hook** in the generated `Step` for the `TF` single-step (and the
  `IF`/`TF`-clear-on-service / `IRET`-restore semantics in the partial). Small, enumerated.
- **`IRET`/`INT`/`INTO`/divide-trap micro-ops** in the 8086 partial + interpreter bodies; JIT
  fallback for the trap-heavy ones initially (the staged approach the Z80 ADR endorses,
  `0001-…:386-391`).

**Verdict (interrupts):** the seam generalizes to a third model with **no Core contract change** for
the line-driven part — a genuine genericity win, the second positive finding (after I/O). The only
new shape is the **post-instruction `TF` trap hook** (a small generated-layer extension) and the
*instruction-as-interrupt* micro-ops (`INT n`/divide-error), which are ordinary new vocabulary.

---

## 7. Validation — SingleStepTests 8088 vectors

### 7.1 Availability and what they check

The **SingleStepTests 8088** set exists and is the M5 acceptance gate (research doc names it,
`emulation-framework-research.md:27, 216-217, 227`; the design spec's M5 line implies it,
`…framework-design.md:275-277`). Verified facts (sources at end):

- **Hardware-generated** on a real **AMD D8088 (1982)** running in **maximum mode**, with bus signals
  via i8288 emulation. Language-agnostic JSON, the same format family as the 6502/Z80/68000 sets the
  TomHarte harness already consumes (`…framework-design.md:177-184` describes the generic harness:
  load JSON, set initial state via introspection, run one instruction against a recording bus, diff
  final state + cycle count + **per-cycle bus trace**).
- **~300+ opcode forms, > 3 million tests, ~90 million cycles.** 10,000 tests/opcode (with caps:
  string ops 2,000; CL-shift/rotate 5,000; fixed-register INC/DEC 1,000; flag ops 1,000).
- Each opcode entry has a **`status`** field: `normal` / `prefix` / `alias` / `undocumented` /
  `undefined` / `fpu`. This directly informs M5 scope: emulate `normal`/`alias`; decide on
  `undocumented` (well-defined, like the Z80 X/Y question, `0001-…:700-701`); `undefined` (unpredictable)
  and `fpu` (the 8087 coprocessor escape) are reasonable deferrals (mirroring the 6502 illegal-opcode
  deferral, `…framework-design.md:297`).
- All tests assume a **full 1 MB** of writable RAM, address wrap at `$FFFFF`, **no wait states**.

### 7.2 The 8088-vs-8086 distinction (flag for M5)

**Critical M5 caveat: the vectors are *8088*, not *8086*.** The 8088 has an **8-bit external data
bus** (the 8086 has 16-bit). Architecturally (registers, flags, instruction semantics, segmentation)
they are *identical*; the difference is **bus-cycle traces**: a 16-bit memory access on the 8088 is
**two 8-bit bus cycles**, where on the 8086 it would be one 16-bit cycle (when aligned). The 8088 also
has a **4-byte prefetch queue** vs the 8086's 6-byte.

Implications for our **per-cycle bus-trace** validation (the harness diffs the bus trace, not just
final state, `…framework-design.md:180`):

- The 8088 vectors' bus traces reflect **8-bit bus cycles + 4-byte-queue prefetch timing.** If M5
  builds an **8088** core (8-bit bus), it can aim for full per-cycle bus-trace fidelity against the
  vectors. If M5 builds an **8086** core (16-bit bus), the *final state + flags + register results*
  will match the vectors, but the **per-cycle bus trace will differ** (cycle counts and access widths
  diverge). 
- **Recommendation:** target the **8088** for M5 (it is what the vectors capture), exactly as the
  research doc's "8088≈8086" framing (`emulation-framework-research.md:27`) and §7 note
  ("Validate against TomHarte **8088**", `:217`) imply. The 16-bit-bus 8086 can be a later variant.
  Set the **accuracy bar** the same way the Z80 ADR asks (open-question 5, `0001-…:705-707`):
  per-cycle bus-trace fidelity (the 8088 vectors' bar) vs instruction-cycle-count fidelity — decide
  explicitly. The prefetch queue is the hard part of *per-cycle* fidelity (the queue refills
  opportunistically, so the exact fetch-cycle interleaving is queue-state-dependent — this is
  materially harder than the 6502's "one cycle = one bus access" and even the Z80's M-cycle model;
  see the cited righto.com prefetch articles).

### 7.3 What must grow

- **Importer loader extensions first** (the runbook's stated per-family pattern,
  `extraction-runbook.md:188`): new mode vocabulary (ModR/M family, §4), the variable-length opcode-key
  format (§3), new factories (§5) in `SemanticsMap.FactoryArity` (`SemanticsMap.cs:44-82`) +
  `SpecFileEmitter.SupportedModes` (`SpecFileEmitter.cs:41-47`) + the generator mirror tables
  (`SpecParser.cs:38-143`). The **cross-source diff** rung (`extraction-runbook.md:207-217`) is even
  more load-bearing here than for the Z80 (the 8086 has `d`/`w`-bit families and opcode groups that
  are easy to mis-transcribe).
- **The TomHarte harness already generalizes** (it is `ICpuCore`-generic, `…framework-design.md:177`),
  so wiring the 8088 set is harness-config, not harness-rewrite — *provided* the harness can express
  the 1 MB / 20-bit space and the `status`-field opcode filtering.

---

## 8. JIT genericity implications — which 6502/Z80 JIT-isms the 8086 stresses

The Z80 ADR's Decision 7 (`0001-…:497-528`) enumerates the JIT's 6502-shaped assumptions (J1–J10).
The 8086 stresses a specific subset hardest, plus adds new ones:

| JIT-ism | Z80 status | How the 8086 stresses it |
|---|---|---|
| **J1: `BlockCompiler` typed to `Mos6502Cpu`** (`BlockCompiler.cs:16, 69, 97`) | M3.5 makes it generic over the CPU type | The 8086 just needs J1 done (it is on the M3.5 path); no *new* pressure beyond "a third CPU type." |
| **J2: register file as baked `FieldInfo`s** (`BlockCompiler.cs:37-42, 454-458`) | M3.1a turns it into a by-name map (`m3a-register-file.md:48-51`) | **AL/AH aliasing (§1):** the by-name map must resolve `AL`/`AH` to the **same backing `AX` field + shift/mask**, not distinct fields. A naive "one FieldInfo per register name" map (which M3.1a builds) breaks if `AL` and `AX` are separate cells. The emitted IL for a write to `AL` must read-modify-write the `AX` field's low byte. **New emit pressure the Z80's `B`/`C` halves *also* create — design the aliased-register IL once for both.** |
| **J3: single-byte `JitDescriptors[opcode]` index + static `d.Length`** (`BlockCompiler.cs:81, 84`; `OpcodeDescriptor.cs:41`) | M3.1b replaces with a decode walk; discovery advances by the walk's length (`0001-…:167-168, 507`) | **The worst case (§3).** Variable-length ModR/M decode means **block discovery itself must run the full decoder** — `Discover` (`BlockCompiler.cs:75-87`) cannot index a table and read a length; it must consume prefix+opcode+modrm+disp+imm to know where the next instruction starts. **This is the JIT-ism the 8086 stresses most**, and it is the same thing as the §3 decode question viewed from the JIT's block-walk. If M3.1b's decode walk returns a computed length, J3 generalizes; if it returns a table slot with a static length, the JIT block walk breaks at M5. |
| **J4: fastmem byte arms on a flat `uint` EA** (`BlockCompiler.cs:277-419`) | Z80 reuses unchanged (a positive finding, `0001-…:508`) | **Segmentation (§2):** the fastmem arm is unchanged; only the **EA computation upstream** changes (emit `(seg<<4)+offset` instead of `lo|(hi<<8)`). **Verified the fastmem split is physical-address-based and survives.** The one risk is the `Conv_U2` audit (§2.2 item 1): the physical EA must stay 20-bit `uint`, not be truncated to 16-bit. Also, 16-bit memory accesses are **two byte accesses** (composable from the existing byte helpers, like the Z80's 16-bit loads, `0001-…:508`) — on the 8088 that is authentic (8-bit bus); on a 16-bit 8086 it would be one access. |
| **J5: cycle templates / `PageCrossPenalty`** (`OpcodeDescriptor.cs:42`; `BlockCompiler.cs:223`) | Z80 loosens the one-cycle-per-access model (`0001-…:509`) | The 8086/8088 timing is **even less "one cycle = one bus access"** — the prefetch queue decouples fetch cycles from execution (§7.2). `PageCrossPenalty` is meaningless; cycle cost is a per-instruction (queue-state-dependent for exact fidelity) number. The descriptor's cycle model must already be "carry the instruction's cycle count" (the Z80 forces this); the 8086 adds queue-dependent fetch timing as the hardest *cycle-accurate* case — another reason to set the accuracy bar deliberately (§7.2). |
| **J7: decimal arm** (`BlockCompiler.Decimal.cs`) | Z80 makes it dead code (`DAA` is a different arm, `0001-…:511`) | Same as Z80: the 8086's BCD is `DAA/DAS/AAA/…` using the `A`/aux flag, **not** a `D`-flag-gated ADC. The 6502 decimal arm is dead code; the 8086 BCD adjusts are their own micro-ops/arms. Confirms BCD must be a spec-declared capability, not a fixed JIT feature. |
| **J9: block-ending classification** (`OpcodeDescriptor.EndsBlock`; `BlockCompiler.Flow.cs`) | Z80 adds conditional CALL/RET/JR/DJNZ/RST/block-ops/HALT (`0001-…:513`) | The 8086 adds `Jcc`×16, `LOOP/LOOPE/LOOPNE`, `JCXZ`, near/far/indirect `CALL`/`JMP`/`RET`, **string-op REP self-repeat** (a one-instruction loop, like Z80 block ops), `HLT`, and **`INT n` (a static-vector control transfer — chainable like the Z80 `RST`)**. The far/indirect transfers have **dynamic targets** (segment+offset from memory/registers) → not chainable, end the block (the existing dynamic-exit path). The REP string ops are JIT-fallback candidates initially (like Z80 block ops). |
| **J10: `JitOp` operand shape `(RegA,RegB,FlagBit,BoolArg)`** (`OpcodeDescriptor.cs:32`) | Z80 needs an extensible operand model for the bit-index (`0001-…:514`) | **The 8086 makes the extensible/decoded operand model mandatory (§5.2):** operands are decoded from ModR/M `reg`/`r/m` + opcode `d`/`w` bits, not baked indices. The `JitOp` struct must carry *decoded operands* (or a reference to the decoded ModR/M), not a fixed 4-field shape. This is the same change the Z80's bit-index begins; the 8086 forces it to completion. |
| **NEW — segmentation in emitted address math** | n/a (no Z80/68000 analog) | The emitted EA computation gains a `(segReg<<4)+offset` step with per-operand segment selection. Localized to the 8086 emit arm; the downstream fastmem arm is unchanged (J4). |
| **NEW — aliased-register IL (AL/AH ⊂ AX)** | Z80 `B`/`C` halves are the same shape | Emitted reads/writes of `AL`/`AH` must mask/shift the shared `AX` field. Design once for Z80 halves + 8086 halves. |

**Verdict (JIT):** the 8086 stresses **J3 (variable-length block discovery)** hardest — and it is the
*same* §3 decode question. **J1/J2 are on the M3 path already; J4 (fastmem) survives segmentation
unchanged (positive finding); J10 (operand model) is forced to completion.** The two genuinely new
emit concerns — segmentation address math and AL/AH aliasing — are both localized (segmentation to
the 8086 arm; aliasing shared with the Z80's half-registers).

---

## 9. Opcode-space structural map

> **PROVISIONAL · STRUCTURAL-ONLY · UNVERIFIED PENDING M5 EXTRACTION.** This is the encoding
> *grammar* and the *group structure*, not a byte-accurate table. Byte-accurate extraction is M5's
> job, gated by the runbook's cross-source diff (`extraction-runbook.md:207-217`). Counts below are
> structural estimates for scoping only.

### 9.1 The encoding grammar

```
instruction ::= prefix* opcode modrm? disp? imm?

prefix      ::= 0xF0 (LOCK)
              | 0xF2 (REPNE) | 0xF3 (REP/REPE)
              | 0x2E (CS:) | 0x36 (SS:) | 0x3E (DS:) | 0x26 (ES:)        ; segment override
              ; (0x66/0x67 operand/address-size prefixes are 80386+, NOT 8086)

opcode      ::= 1 byte                                                    ; 8086: effectively 1-byte
              ; some opcodes embed operands in low bits:
              ;   ALU block: 0b<alu:3><d:1><w:1>  (d=direction, w=width)
              ;   reg short forms: 0x40-0x4F INC/DEC reg, 0x50-0x5F PUSH/POP reg,
              ;                    0x90-0x97 XCHG AX,reg, 0xB0-0xBF MOV reg,imm

modrm       ::= [ mod:2 | reg:3 | rm:3 ]                                  ; present for ModR/M opcodes
              ; reg = 2nd register operand OR opcode-group extension
              ; (mod,rm) selects register (mod=11) or one of 24 16-bit EA forms

disp        ::= absent           if mod=11, or (mod=00 and rm!=110)
              | disp8  (1 byte)   if mod=01
              | disp16 (2 bytes)  if mod=10, or (mod=00 and rm=110 direct)

imm         ::= absent | imm8 (1 byte) | imm16 (2 bytes)                  ; per opcode + width w
              ; far ptr: imm16:imm16 (offset:segment) for CALL FAR / JMP FAR direct
```

**Length = 1..6 bytes, computed by the walk** (the §3 crux). **No SIB byte** (16-bit mode). The
`reg` field doubling as an **opcode-group extension** (groups `0x80-0x83`, `0xD0-0xD3`, `0xF6/0xF7`,
`0xFE/0xFF`) means the decode key for those opcodes is **`opcode<<3 | modrm.reg`**.

### 9.2 Opcode groups (structural)

- **Data movement:** MOV (many forms), PUSH/POP (reg/mem/seg), XCHG, XLAT, LEA, LDS/LES, LAHF/SAHF,
  IN/OUT (port I/O — §below).
- **ALU (`d`/`w`-bit blocks):** ADD OR ADC SBB AND SUB XOR CMP (each: r/m←reg, reg←r/m, AL/AX←imm,
  and the `0x80-0x83` immediate-to-r/m group).
- **Shift/rotate group (`0xD0-0xD3`, reg-extension):** ROL ROR RCL RCR SHL SHR SAR (by 1 or CL).
- **Inc/dec/call/jmp/push group (`0xFE/0xFF`, reg-extension)** + INC/DEC reg short forms.
- **Mul/div/not/neg/test group (`0xF6/0xF7`, reg-extension):** TEST NOT NEG MUL IMUL DIV IDIV.
- **String + REP:** MOVS CMPS SCAS LODS STOS (×{8,16}), with REP/REPE/REPNE.
- **Control transfer:** Jcc (16 conditions, rel8), JMP/CALL (near rel16 / near indirect / far direct
  ptr16:16 / far indirect), RET/RETF (±imm16), LOOP/LOOPE/LOOPNE, JCXZ.
- **Flags:** CLC STC CMC CLD STD CLI STI PUSHF POPF.
- **BCD/adjust:** DAA DAS AAA AAS AAM AAD CBW CWD.
- **Interrupt:** INT n, INT 3, INTO, IRET (+ implicit type-0 divide, type-1 single-step).
- **Port I/O:** IN/OUT (fixed-port imm8 and DX-indirect, ×{8,16}) → the separate I/O space.
- **CPU control:** NOP HLT WAIT LOCK ESC(fpu — out of scope).

### 9.3 Provisional count

The SingleStepTests 8088 set reports **~300+ opcode forms** (§7.1, verified). Counting the `d`/`w`-bit
expansions, the reg-extension groups, and the implicit/short forms, the *documented* encoding space is
on the order of **~300 primary opcodes** before the ModR/M operand-form fan-out (×24 EA forms per
ModR/M opcode, but those are **decoder-resolved, not table rows** — §4). So: **~300 spec rows
(provisional)**, far fewer than the Z80's ~1000+ *because the 8086's regularity lives in the ModR/M
byte and the `d`/`w` bits rather than in distinct opcodes* — the complexity moves from *table size*
(Z80) to *decode logic* (8086). **Mark explicitly: provisional, structural-only, unverified pending
M5 extraction.**

---

## 10. What this means for M3 NOW

The 8086 is M5, but two of its pressures land on decisions being made in M3 **this milestone** — and
getting them right now is free, while retrofitting them at M5 is expensive.

### 10.1 The M3.1b generic decoder — the highest-leverage forward input

**M3.1b is the decode/prefix chunk, and it is not yet built** (`m3a-register-file.md:61-66`). The
design has *already committed* to the general decoder (option (B), the multi-byte-key state machine,
`0001-…:134-143`; `…framework-design.md:265`) precisely because it must "extend to the 8086's
variable-length prefixed encoding." **This brief's single most important finding is that committing to
option (B) is necessary but not sufficient — the implementation must additionally make instruction
*length* a COMPUTED OUTPUT of the decode walk, not a static field on a descriptor.**

Concretely, M3.1b should ensure:

1. **`Discover`/`Step` advance PC/IP by the decoder's *returned* length, not by a static
   `OpcodeDescriptor.Length`.** Today `BlockCompiler.cs:84` does `pc += d.Length` reading a fixed
   `OpcodeDescriptor.Length` (`OpcodeDescriptor.cs:41`). The Z80 ADR already says discovery must use
   "the decode function's total length" (`0001-…:167-168, 507`). **M3.1b must implement that as a
   genuine computation, even though the Z80's own lengths are table-derivable** — because if the Z80
   implementation cheats (stores a per-key static length), the 8086's ModR/M-dependent length breaks
   it. **Build the decoder so length is computed by consuming bytes, and let the Z80 be the
   easy case of that machine.**

2. **The decode walk's state model must accommodate a "operand byte that determines remaining
   length" state** — i.e. the ModR/M state, where reading one byte determines how many *more* bytes
   (displacement) follow. The Z80's `DDCB dd op` (displacement *before* opcode) is a fixed-position
   data byte; the 8086's ModR/M is a *length-determining* data byte. **A decoder that can only express
   "fixed prefix bytes → opcode → fixed-length operand" will not survive M5.** Express decode as a
   small per-byte state machine: `consume prefixes → consume opcode → (opcode-says-modrm?) consume
   modrm → (modrm-says-disp-size) consume disp → (opcode-says-imm-size) consume imm`. The Z80 is the
   degenerate case (no modrm; fixed operand counts).

3. **The decode key can include a sub-field of a non-first byte.** The 8086's opcode-group encodings
   key on `opcode<<3 | modrm.reg`. The multi-byte key (option B) should be flexible enough that the
   key is "whatever bytes/bit-fields select the operation," computed by the walk — not "the first N
   whole bytes." This is a small but real constraint on the key model.

**If M3.1b honors these three properties, the 8086 slots in as "a longer walk with a ModR/M state."
If it ships a multi-byte-key-into-fixed-length-descriptor design, M5 requires a decode-strategy
rethink.** This is the corner to avoid painting M3 into.

### 10.2 Other M3 inputs (lower leverage, but cheap to honor now)

- **Register aliasing (§1):** M3.4 must implement Z80 half/pair aliasing (`B`/`C` ⊂ `BC`). **Design
  the `RegisterDef` alias relationship to be bidirectional** (whole→halves for the 8086's `AL`/`AH` ⊂
  `AX`, halves→whole for the Z80's `B`+`C` = `BC`) so M5 inherits it. The JIT's by-name `FieldInfo`
  map (M3.1a J2, `m3a-register-file.md:48-51`) must already, for the Z80, resolve aliased names to a
  shared backing field + shift/mask — **the 8086 needs the identical IL pattern; build it once.**
- **The operand model (§5.2, J10):** the Z80's bit-index operand already forces `JitOp` to grow past
  its fixed `(RegA,RegB,FlagBit,BoolArg)` shape (`0001-…:396-397, 514`). **Design the extended operand
  model to carry *decoded* operands** (ModR/M-derived), because the 8086 makes decoded operands the
  norm, not an exception. Doing this generally in M3.5 (when J10 is addressed for the Z80) saves a
  second pass at M5.
- **The flag vocabulary (§1.3):** M3.4's composable flag micro-op family (`SetSZ`/`SetHalfCarry`/
  `SetParity`/`SetOverflow`) should be designed so the 8086's `A`(aux)/`O`/`P` reuse it; the only
  8086-specific additions are the control-bit ops (`SetDirection`/`SetTrap`). Reusable if M3.4 builds
  the family generally (`0001-…:301-308`).

### 10.3 What M3 should explicitly NOT do for the 8086

Per the project's scope discipline (`emulation-framework-research.md:251-252`; the Z80 ADR's
open-question 7, `0001-…:714-718`): **do not pre-build segmentation, ModR/M, or the 20-bit space in
M3.** Those are M5 and are cleanly localized to the 8086 partial/spec (confirmed in §2/§4 — they do
not touch the bus or JIT contracts). The *only* M3-time inputs are the **decoder shape (§10.1)** and
the **shared abstractions (aliasing, operand model, flag family) designed general enough to inherit
(§10.2)** — all of which M3 needs *anyway* for the Z80. The 8086 does not add M3 work; it adds M3
*design constraints* on work already being done.

---

## 11. M5 risk list & open questions

**Lead risk (the one M3 can de-risk now):**

1. **Does the generic decoder survive ModR/M variable-length, or is a decode-strategy rethink needed
   before M5?** — **Verdict: it survives IF AND ONLY IF M3.1b makes instruction length a computed
   output of a per-byte-consumption decode walk (not a static descriptor field), and models a
   length-determining mid-stream byte (ModR/M).** The project has committed to the right *option*
   (multi-byte-key state machine, B); the risk is in the *implementation* taking the shortcut of a
   fixed-length descriptor table keyed by a multi-byte key. **This must be checked at M3.1b design
   time** (§3, §10.1). If M3.1b ships the shortcut, M5 needs a decode rethink — the single biggest M5
   risk. *Recommended action: review the M3.1b plan against the three properties in §10.1 before it is
   implemented.*

**Other M5 risks/open questions:**

2. **8088 vs 8086 — which to build, and the accuracy bar.** The vectors are 8088 (8-bit bus, 4-byte
   queue). Build the **8088** for vector fidelity; the **prefetch queue** makes *per-cycle* bus-trace
   fidelity materially harder than the 6502/Z80 (queue-state-dependent fetch interleaving). **Open:
   per-cycle bus-trace fidelity (the vectors' bar) vs instruction-cycle-count fidelity?** (§7.2; same
   shape as the Z80 ADR's open-question 5, `0001-…:705-707`.) The 16-bit 8086 is a later variant.

3. **Undocumented/undefined opcode policy.** The 8088 vectors mark opcodes
   `normal`/`alias`/`undocumented`/`undefined`/`fpu`. **Open: gate M5 on `undocumented` fidelity, or
   defer like the 6502 illegal opcodes?** (§7.1; mirrors the Z80 X/Y open-question,
   `0001-…:700-701`.) `fpu` (8087 escape) and `undefined` are reasonable deferrals.

4. **Register-alias model — bidirectional?** Confirm M3.4 designs the `RegisterDef` alias relationship
   to express *both* whole→halves (8086 `AL`/`AH`) and halves→whole (Z80 `B`/`C`), and that the JIT's
   aliased-register IL (shared backing field + shift/mask) is built once for both (§1, §8 J2). A real
   small Core change (a new `RegisterDef` field) — flag as a shared M3↔M5 abstraction.

5. **Operand model completion.** Confirm M3.5's J10 fix carries *decoded* operands (ModR/M-derived),
   not just an extended-but-still-baked shape, so the 8086 reuses it (§5.2, §8 J10).

6. **Post-instruction trap hook (`TF` single-step) + instruction-as-interrupt micro-ops.** The `TF`
   trap needs a *post*-instruction service hook in generated `Step` (the current seam is *pre*-fetch);
   `INT n`/divide-error raise interrupts inline. Small enumerated generated-layer change; the
   line-driven `INTR`/`NMI` reuse the existing partial seam unchanged (§6 — a positive finding).

7. **The 20-bit physical EA `Conv_U2` audit.** Verify no emitted/interpreter address path truncates
   the physical EA to 16 bits; the offset wraps at 16 bits but `(seg<<4)+offset` is 20-bit
   (§2.2 item 1, §8 J4). Localized audit, not a contract change.

8. **Cross-source extraction load.** The 8086's `d`/`w`-bit families and reg-extension opcode groups
   are easy to mis-transcribe; budget the runbook's cross-source diff rung generously
   (`extraction-runbook.md:207-217`), as the Z80 ADR advises for ~1000 rows (`0001-…:488-490`) — here
   the row count is lower (~300) but the *encoding regularity* (operands in opcode bits) is a new
   class of extraction error.

---

## 12. Summary — the three biggest seams the 8086 stresses beyond Z80/68000

1. **Variable-length ModR/M decode (the worst case for the M3.1b decoder).** Length is data-dependent
   on a mid-stream byte; the decoder must compute length by consuming bytes, not read it from a slot.
   This is the highest-leverage forward input and the single biggest M5 risk. *(§3, §8 J3, §10.1,
   risk 1.)*

2. **Segmentation — a new CPU-internal addressing layer (`seg<<4 + offset`).** New beyond *both* the
   Z80 (flat 16-bit) and the 68000 (flat 24-bit, big-endian). **But contained:** it is CPU-internal
   address math producing a flat 20-bit physical address the bus already handles; `IAddressSpace` and
   the JIT fastmem need no segmentation awareness (verified) — only a `uint`-wide physical EA pipeline
   and per-operand segment selection in the EA micro-ops. *(§2, §8 J4, §8 NEW, risk 7.)*

3. **Register aliasing (AL/AH ⊂ AX) + decoded operands (ModR/M `reg`/`r/m`, opcode `d`/`w` bits).**
   The register file gains an overlap dimension our model lacks (the same shape as the Z80's `B`/`C` ⊂
   `BC`, in the opposite storage direction), and operands become *decoded* rather than baked — forcing
   the extensible operand model the Z80's bit-index only began. *(§1, §5.2, §8 J2/J10, risks 4-5.)*

The 68000 (M4) tested register-width + big-endian + word/long bus + 24-bit address; the Z80 (M3)
tested decode structure + register file + flag model + block/chain. **The 8086 (M5) tests the two
dimensions left: data-dependent variable-length decode and non-flat (segmented) addressing** — exactly
the "second half of the genericity proof" the Z80 ADR's §3 verdict (`0001-…:636-654`) and the
three-architecture-ladder checkpoint (`0001-…:658-673`) name. Once M5 lands, essentially no genericity
dimension is untested before the M6 cross-architecture JIT optimization.

---

## Sources

**Repo (primary):**
- `docs/architecture/0001-z80-second-architecture.md` — the Z80 ADR; per-seam genericity-audit method,
  Decision 1 (decode), Decision 3 (register/flag), Decision 5 (interrupts), J1-J10 JIT audit, §3
  verdict, the three-architecture-ladder checkpoint.
- `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md` — §7 8086 note (:216-217), §9
  M5 line (:275-277), the I/O multi-space day-one decision (:63), the tiered-accuracy decision (:28).
- `docs/research/emulation-framework-research.md` — §7 8088 note (:216-217), TomHarte/SingleStepTests
  (:27, 227-232), the fastmem/SMC dynarec patterns (:153-174), scope-discipline risk (:251-252).
- `docs/superpowers/plans/2026-06-13-m3a-register-file.md` — what M3.1a does (data-driven register
  file) and explicitly excludes (M3.1b decode :61-66; flag model :67-70; J1 :75-79).
- `docs/user-guide/extraction-runbook.md` — the loader-extension-first per-family pattern (:188), the
  verification ladder incl. cross-source diff (:190-254).
- `src/CpuEmulator.Core/Specification/` — `AddrMode.cs:6-12`, `Flag.cs:5-13`, `InstructionDef.cs:7`,
  `RegisterDef.cs:6`, `RegisterRole.cs:3-9`, `Op.cs:8-46`, `Spec.cs:7-9, 15-52`.
- `src/CpuEmulator.Core/AddressSpace.cs` — flat physical addressing, `addressBits ≤ 24` cap (:33-36),
  `TryGetDirectAccess` fastmem seam (:131-145). `AddressSpaceKind.cs:8-13` (Program/Data/Io, names
  Z80/8086). `ICpuCore.cs:30-42` (line inputs + introspection).
- `src/CpuEmulator.Core/Jit/OpcodeDescriptor.cs` — the descriptor model; static `Length` (:41),
  `JitMode` closed set (:19-25), `JitOp` fixed operand shape (:32).
- `src/CpuEmulator.Jit/BlockCompiler.cs` — single-byte `Discover` + `pc += d.Length` (:75-87, :84),
  baked `FieldInfo`s (:37-42), `RegField` (:454-458), fastmem byte arms (:277-419), interrupt sampling
  at chain edge (:496-499), fallback-to-interpreter valve (:541-563), `PagesSpanned` (:123-131).
- `src/CpuEmulator.Generators/SpecParser.cs` — mirror tables + class/mode matrix (:15-143, :399-644),
  `RequiredIndexRegister` 6502-ism (:580-585), single-byte opcode range check (:352-358), retired
  `s_regMembers` note (:25-28).
- `tools/CpuEmulator.SpecImporter/` — `OpcodeDataset.cs` single-byte `OpcodeFormat` (:45-46) +
  `ExpectedBytes(mode)` length-from-mode (:146-153); `SemanticsMap.cs:44-82` (FactoryArity);
  `SpecFileEmitter.cs:41-47` (SupportedModes).
- `src/CpuEmulator.Cpus.Mos6502/Mos6502Cpu.cs` — the interrupt-seam partial (`InterruptPending` :69,
  `TryServiceInterrupt` :78-100), `ReadBus(uint)` :111, `AdvanceCycles` JIT cycle seam :109.

**External (8086/8088 architecture, verified June 2026):**
- ModR/M structure, 16-bit EA forms (24 forms from 5 mod+rm bits), no SIB in 16-bit mode:
  <https://wiki.osdev.org/X86-64_Instruction_Encoding>,
  <https://datacadamia.com/lang/assembly/intel/modrm>
- 8086 ModR/M addressing microcode + instruction-length determination (no architected length limit;
  prefetch-queue-bounded): <http://www.righto.com/2023/02/8086-modrm-addressing.html>,
  <http://www.righto.com/2023/02/how-8086-processor-determines-length-of.html>
- 8086 registers (AX/AL/AH halves), segment registers CS/DS/SS/ES, FLAGS 9 active bits, default
  segments (DS general, SS for BP/SP-based, ES for string dest):
  <https://www.geeksforgeeks.org/electronics-engineering/types-of-registers-in-8086-microprocessor/>,
  <https://www.geeksforgeeks.org/types-of-flags-in-8086/>,
  <https://www.righto.com/2023/02/silicon-reverse-engineering-intel-8086.html>
- SingleStepTests 8088 set (hardware-generated on AMD D8088, maximum mode, ~300+ opcode forms, >3M
  tests, 10k/opcode with caps, `status` field normal/prefix/alias/undocumented/undefined/fpu, 1 MB
  RAM, wrap at $FFFFF): <https://github.com/SingleStepTests/8088>,
  <https://github.com/SingleStepTests/ProcessorTests>,
  <https://martypc.blogspot.com/2023/09/a-test-suite-for-intel-8088.html>
- 8088 vs 8086 differences (8-bit vs 16-bit bus, 4- vs 6-byte prefetch queue):
  <https://www.scs.stanford.edu/05au-cs240c/lab/i386/s15_06.htm>
