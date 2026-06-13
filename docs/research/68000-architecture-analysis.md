# Forward Architecture Research — Motorola 68000 (Milestone M4)

> **Editor's note (2026-06-13):** this brief was drafted reading the `feat/m3-register-file`
> working tree, which predates **ADR 0002 (address-space scaling)**. ADR 0002 now exists on `main`
> and confirms this brief's analysis: the 68000's 24-bit address bus fits the `addressBits ≤ 24`
> cap, so no two-level page table is needed for M4. Where the brief says "ADR 0002 does not exist,"
> read "see ADR 0002 — consistent."
>
> **Status:** Research / forward-looking architecture brief — **READ-ONLY analysis**, no implementation.
> **Date:** 2026-06-13
> **Author posture:** mirror of the genericity-audit method in ADR 0001
> (`docs/architecture/0001-z80-second-architecture.md`) — per-seam, "where is the framework shaped
> for what it has *seen*, and what does THIS cpu *demand*."
> **Purpose:** (a) de-risk and front-load M4 (68000), and (b) feed concrete constraints back into
> the **in-flight M3 generalizations** (M3.1a data-driven registers landing now; M3.1b generic
> decoder next; M3.2 bus/interrupt seams) so they do not bake in a Z80/6502 bias that M4 then has
> to unwind.
> **Relates to:** the framework design spec
> (`docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`, §7 per-CPU note + §9 the
> M3→M4→M5→M6 ladder), the research doc (`docs/research/emulation-framework-research.md`, §7), ADR
> 0001 (the Z80 decision record, esp. its §3 "what Z80 does NOT prove" and the 2026-06-13
> human-checkpoint that gated optimization behind **three** architectures), and the M3.1a plan
> (`docs/superpowers/plans/2026-06-13-m3a-register-file.md`).

> **A note on scope and honesty.** This brief is *structural*. It analyses the 68000 against OUR
> seams from the real source (cited by file:line below). Everything about the 68000's encoding
> grammar, opcode counts, and instruction groupings is **provisional, structural-only, and
> unverified-pending-M4-extraction** — M4's extraction job (cross-source diff + TomHarte) is what
> turns this map into a byte-accurate truth. Where I am uncertain I say so.

> **One correction up front.** The brief that commissioned this analysis cites
> `docs/architecture/0002-address-space-scaling.md` for the `addressBits<=24` analysis. **That ADR
> does not exist in the tree** (only `0001-z80-second-architecture.md` is present). The
> `addressBits<=24` analysis actually lives **inside ADR 0001** — in the source
> (`src/CpuEmulator.Core/AddressSpace.cs:34`, the constructor cap) and in ADR 0001's open question 7
> (`0001-…:714-718`) and the 2026-06-13 human-checkpoint section (`0001-…:658-673`), which states
> plainly: "Both 68000 (24-bit address bus) and 8086 (20-bit physical) fit the current
> `addressBits ≤ 24` design, so neither forces the deferred two-level page table." I confirm this
> below (§2) and cite the real source.

---

## 0. The 68000 in one paragraph (what makes it different from everything we have seen)

The Motorola 68000 is a **16/32-bit** CISC microprocessor: a **32-bit programming model**
(registers and the logical address computation are 32-bit) sitting behind a **16-bit external data
bus** and a **24-bit address bus** (`A1..A23` + two byte-strobe lines `UDS`/`LDS`; `A0` does not
exist as a pin — byte selection is done by the strobes). It has **eight 32-bit data registers**
`D0–D7` and **eight 32-bit address registers** `A0–A7`, where `A7` is the stack pointer and is
*banked* into a User Stack Pointer (USP) and a Supervisor Stack Pointer (SSP/ISP) by the
supervisor-mode bit. Operations carry a **size suffix** — `.b` (byte/8), `.w` (word/16), `.l`
(long/32) — applied to the *same* register, which is an axis our model has never had. It is
**big-endian** (the high-order byte is at the lower address). The status register `SR` splits into a
user-visible **CCR** (`X N Z V C` — note the **X**/extend flag, a second carry the 6502/Z80 lack)
and a supervisor byte (trace bit `T`, supervisor bit `S`, and a 3-bit **interrupt mask** `I0–I2`).
Memory access is **word-oriented and alignment-checked**: word and long accesses must be
even-aligned or the CPU takes an **address-error exception**; a long access is two word bus cycles.
The ISA is **large but extraordinarily regular** — operation, size, addressing mode, and register
are encoded as *fields* of a single **16-bit instruction word**, with **extension words** following
for immediates, displacements, and the full/brief indexed-mode descriptors. It has **7 prioritized
interrupt levels**, a **256-entry exception vector table at address 0**, supervisor/user mode
separation, and TRAP/privilege/bus-error/address-error exceptions. There are SingleStepTests
(TomHarte) `m68000` vectors (research §8 cites them).

Every italicised word above is a seam this brief audits.

---

## 1. Register & data model

### 1.1 What the 68000 needs

- **8 data registers `D0–D7`** (32-bit) and **8 address registers `A0–A7`** (32-bit), `A7` = SP.
- **`A7` is banked**: USP (user) and SSP (supervisor), selected by `SR.S`. A move to/from `A7` hits
  whichever bank the current mode selects; there are privileged `MOVE USP` forms to reach the other
  bank explicitly.
- **`SR`** = a 16-bit status register: low byte = **CCR** (`C`=bit0, `V`=1, `Z`=2, `N`=3, `X`=4),
  high byte = system byte (`I0–I2`=bits 8–10 interrupt mask, `S`=bit13 supervisor, `T`=bit15 trace).
- **`PC`** is 32-bit logically but only 24 bits reach the bus.
- **The size dimension `.b/.w/.l`**: a `MOVE.B`, `MOVE.W`, `MOVE.L` all target the *same* `D0`, but
  touch the low 8 / low 16 / all 32 bits. Critically:
  - **Data-register byte/word ops are *partial* writes**: `MOVE.W #x,D0` changes only `D0[15:0]`,
    leaving `D0[31:16]` intact. This is unlike anything we model — our registers are written whole.
  - **Address-register ops are *special*:** there is no `.b` for address registers; a `.w` operation
    on an `An` is **sign-extended to 32 bits** and writes the *whole* register; `MOVEA` and address
    arithmetic do **not** set CCR (a quirk the generator must not "helpfully" add — directly
    analogous to the Z80 `INC rr` "sets no flags" quirk ADR 0001 Decision 4 calls out at
    `0001-…:347`).

### 1.2 How it maps to / stresses OUR current seams

**The good news: M3.1a already removed the register-identity wall for us.** ADR 0001 Decision 3(ii)
(`0001-…:288-289`) and the M3.1a plan retire the closed `Reg` enum in favour of **register-NAME
strings validated against the spec's `Registers` table**. The source confirms this is *already
landing*: `Op.cs:8-12,22,32,35-36` shows `LoadRegOp(string Target)`, `TransferOp(string Source,
string Target)`, etc. — register args are strings now; `Spec.cs:15-19` shows `Load(string target)`;
`SemanticsMap.cs:35-39` documents the no-`s_regMembers` state; `SpecParser.cs:25-28` confirms the
mirror table is gone. **So the 68000's 16-named-register file (`D0..D7`, `A0..A7`) is expressible as
spec data the moment M3.1a closes** — no Core enum edit, exactly the M3 genericity win.

**The bad news, and the *new* axis the 68000 introduces: SIZE.** This is the dimension the brief
correctly flags as one our 8-bit/16-bit register model does not have. Today:

- `RegisterDef.Bits` is constrained to **8 or 16** (`RegisterDef.cs:3-6`, enforced in the parser),
  and the emitter types a register field as `byte` (8) or `ushort` (16) and **casts on write**.
- A micro-op like `Increment`/`Decrement` hardcodes an **8-bit `(byte)` cast** in *both* tiers — the
  interpreter emitter (per ADR `0001-…:299, J-row J2 background) and the JIT
  (`BlockCompiler.Emit.cs:478, 489` — `OpCodes.Conv_U1`). `SetNZ` bakes the 6502's mask `0x7D` and
  `0x80` sign bit (`BlockCompiler.cs:431,447`). These are **8-bit-only op bodies**.

The 68000 needs the **same `D0` register** to be operated on at three widths *by the opcode*, not by
the register declaration. That is a genuinely new model: **width is a property of the
(instruction × micro-op), not of the register.** Two design options, both real work in M4:

- **(A) Width-tagged micro-ops.** A micro-op carries an operand size: `Add(Size.B)`, `Move(Size.L)`.
  The register is declared once at its full width (32); each op knows how many bits it reads/writes
  and whether the write is partial (data reg `.b/.w`) or full-with-sign-extend (`An.w`). This is the
  cleanest fit for the 68000's field-encoded ISA (the size field is *literally* two bits of the
  instruction word — §map below), and it composes with the data-driven register file.
- **(B) Width-suffixed register *names*.** Model `D0`, `D0W`, `D0B` as distinct names that alias the
  same storage (the Z80 pair-view trick from ADR 0001 Decision 3(A), `0001-…:258-266`). *Rejected
  for the 68000*: the 6502/Z80 pair-view exists because the *halves are independently named in the
  ISA*; the 68000 does **not** name `D0.w` as a separate register — the size is an instruction
  field, not a register name. Modelling it as names would explode the register table (24 phantom
  names) and misrepresent the silicon.

**Recommendation (provisional): (A) width-tagged micro-ops**, with `RegisterDef.Bits` allowed to be
32, and a small `Size` enum threaded through the size-bearing ops. Crucially, **partial-write
semantics** (data-reg `.b`/`.w` preserve the upper bits; `An.w` sign-extends to 32) must be encoded
in the op body — this is the "genuinely new code in the emitter and JIT, not a mirror-table edit"
that ADR 0001 already anticipates for 16-bit Z80 math (`0001-…:299-300`), now generalised to a
three-valued size axis.

**>16-bit registers — the dimension ADR 0001 flagged as untested by Z80.** ADR 0001 §3
(`0001-…:626-629`) names this explicitly: "`RegisterDef.Bits` is capped at 16 … the 68000's 32-bit
registers … are genuinely untested." The 68000 is where `Bits == 32` first appears. The storage type
becomes `uint` (or `int` for sign-extension math); the JIT emits 32-bit IL math (`Conv_U4` / no
truncation) instead of the `Conv_U1` that pervades `BlockCompiler.Emit.cs`. This is the **first time
the emitted IL carries values wider than a byte through a register field** — see §8 (JIT).

### 1.3 What the framework must grow

1. `RegisterDef.Bits` accepts **32** (relax `RegisterDef.cs:3-6` and the parser's 8-or-16 check).
2. A **`Size` operand** on size-bearing micro-ops (a new field on the relevant `Op` records;
   §5 enumerates which ops).
3. Width-aware op bodies in the emitter **and** the JIT, including **partial-write** (data reg) vs
   **whole-write-sign-extended** (address reg) semantics.
4. The **`A7`/USP/SSP bank** — modelled as the supervisor-mode-selected view of one named register,
   handled in the hand-written partial (the bank switch is a mode side effect, exactly the altitude
   ADR 0001 uses for the Z80's `R` refresh and the alternate-set swap, `0001-…:292-294`).
5. **`X` (extend) flag** in the flag vocabulary (see §6 — the flag-vocabulary growth M3 should make
   data-driven now).

---

## 2. Endianness & bus width — **the central M4 seam**

> This is the dimension the 68000 *uniquely* proves for genericity, and the one ADR 0001's verdict
> explicitly says the Z80 leaves untested: "**Misaligned-access / wider-than-byte bus
> transactions** … the JIT bus arms are byte-only (`Read8`/`Write8`). The Z80's 16-bit memory ops
> decompose into two byte accesses, so even *it* does not exercise a true word bus transaction"
> (`0001-…:631-634`).

### 2.1 What the 68000 needs

- **Big-endian.** A 16-bit word at address `A` is `(mem[A] << 8) | mem[A+1]`; a 32-bit long is
  `mem[A]..mem[A+3]` high-to-low. This is the **opposite** of the little-endian assembly the 6502
  and Z80 use, and the opposite of what our bus assembles today.
- **Word and long bus transactions on a 16-bit data bus.** A `.w` access is one bus cycle; a `.l`
  access is **two** word cycles (high word first). The interpreter's cycle accuracy depends on this
  (a long access is genuinely two transactions on the silicon).
- **Even-alignment requirement.** A word or long access to an **odd** address raises an **address
  error** (a specific exception, §6) — *before* the access completes. Byte accesses may be at any
  address. This is a guest-world fault our bus has no concept of.

### 2.2 Where OUR bus is shaped for little-endian byte access

`AddressSpace` is **byte-granular and the endianness is implicit in the CPU code, not the bus**:

- `Read8(uint)` / `Write8(uint, byte)` are the *only* transaction primitives
  (`AddressSpace.cs:76-124`). There is no `Read16`/`Read32`.
- Multi-byte values are assembled **little-endian, in the CPU emitter/JIT**, by composing two
  `Read8`s: e.g. the 6502 absolute EA is `lo | (hi << 8)` in the JIT (`BlockCompiler.Emit.cs:97-103`,
  `EmitReadAbsoluteEa`), and JMP-indirect target assembly is the same shape
  (`BlockCompiler.Flow.cs:517-520`). The *bus* never sees a 16-bit transaction; the **byte order is
  a CPU-side convention** baked into emitted IL.
- `IPeripheral.Read(offset, AccessWidth)` **already carries a width** (`AddressSpace.cs:83,118` pass
  `AccessWidth.Byte`; the contract was widened in M1 "so the 68000 doesn't force a contract break
  later" — design spec §4, `…framework-design.md:66-69`). So the *peripheral* contract is ready; the
  **`AddressSpace` transaction surface and the JIT fastmem arms are not.**
- The JIT fastmem reads/writes **one byte at a time** through `LoadByteFromBus`/`EmitStoreByte`
  (`BlockCompiler.cs:277-419`), indexing `backing[PageOffset[page] + (addr & 0xFF)]` — a byte array,
  byte index, `Ldelem_U1`/`Stelem_I1`.

### 2.3 The honest option analysis

**Option (A): add `Read16`/`Read32`/`Write16`/`Write32` to `IAddressSpace`, with endianness in the
bus.** The bus gains wide transaction primitives; the CPU asks for a word and the bus assembles it.

- *Pros:* a word access is **one** call (matches the silicon's one-bus-cycle word access and makes
  cycle charging natural — one charge per word, two per long); the **even-alignment / address-error
  check lives in one place** (the bus, where the address is); the fastmem fast path can do **one**
  `BinaryPrimitives.ReadUInt16BigEndian(span)` over the backing array instead of two byte loads +
  shift — faster, and the JIT emits one wide load. Endianness becomes a **bus property** (a
  `bool BigEndian` or an `Endianness` on `AddressSpace`), which is exactly "make it data, not a
  6502-ism."
- *Cons:* it is a **real `IAddressSpace` contract change** (the §10 "enumerated and justified" kind).
  Every existing caller and the JIT bus arms must learn the wide path. The fastmem direct-array math
  changes (`Ldelem_U1` → a wide read of `backing` at `PageOffset+...`, byte-swapped for BE). The
  6502/Z80 must keep working — they would either keep using `Read8` (their byte convention is
  correct as-is) or be re-expressed in terms of `Read16` little-endian (a bigger blast radius, not
  recommended for M4).

**Option (B): compose wide accesses from `Read8`, with a per-CPU endianness policy.** Keep the bus
byte-only; the 68000 partial/emitter assembles `(Read8(a) << 8) | Read8(a+1)` for a big-endian word,
mirroring how the 6502 does little-endian today, just with the bytes the other way round.

- *Pros:* **zero `IAddressSpace` contract change** — the strongest possible "Core unchanged" result
  for M4's bus seam; reuses the entire fastmem/SMC/dirty machinery (which ADR 0001 J4/J8 already
  proved generic, `0001-…:508,512`) byte-for-byte; the only new thing is a CPU-side byte-order
  convention, which is already CPU-side today.
- *Cons:* a word access is **two** emitted bus branches (two `LoadByteFromBus` expansions) — more IL,
  and the per-cycle charging must be hand-arranged to look like one word cycle (two byte charges
  vs. one word charge — a **cycle-fidelity** problem, see §7); the **address-error / even-alignment
  check has no natural home** (the bus never sees "this is a word access," so the CPU must check
  alignment itself before composing — workable, but it means the alignment rule is CPU code, not bus
  code); the fastmem fast path stays two byte loads (loses the single-wide-load speed win).

**Recommendation (provisional, and the load-bearing M4 call): Option (A) — add wide transactions to
`IAddressSpace` with endianness as a bus property — but stage it so the 6502/Z80 are untouched.**
Rationale, in the ADR 0001 idiom:

- The whole point of the M3→M4→M5 ladder (the 2026-06-13 checkpoint, `0001-…:658-673`) is that the
  **68000 is the architecture that proves the *memory/addressing half* is generic** — the half the
  Z80 leaves untested. Composing wide accesses from `Read8` (option B) makes M4 *cheap* but
  **dodges the very thing M4 exists to prove**: that the bus abstraction can carry wide,
  byte-ordered, alignment-checked transactions. Option (B) would let us declare M4 "done" while the
  bus is still secretly an 8-bit little-endian-only bus with a CPU papering over it. That is the
  6502-shaped trap, one layer down.
- Option (A) is also where **cycle fidelity and the address-error exception become tractable** — both
  want a single place that knows "this is a word/long access at address X."
- The **6502/Z80 do not change**: they keep calling `Read8`. The wide methods are *additive*. The
  `AccessWidth` already in `IPeripheral` (`AddressSpace.cs:83`) means MMIO word access has a contract
  to land on. This is the "additive, enumerated, justified Core change" the success criterion (design
  spec §10 / `0001-…:27`) explicitly permits.

**The fastmem honesty note (ties to §8 / ADR 0001 J4).** ADR 0001 J4 (`0001-…:508`) says the fastmem
*split* is sound and CPU-agnostic; only the byte-only-ness is 6502/Z80-shaped. Option (A) is what
makes that true: a big-endian word fast path is `BinaryPrimitives.ReadUInt16BigEndian` over the same
`byte[]` page backing — same page table, same dirty/SMC machinery, **wider load**. The page model
(256-byte pages, `AddressSpace.cs:10`) does not change; the *element width of the access* does.

---

## 3. Decode structure — word-granular + extension words

### 3.1 What the 68000 needs

The 68000 decodes a **stream of 16-bit big-endian words**:

- The **first instruction word** encodes operation + size + addressing-mode + register **as bit
  fields** (the encoding grammar — §"opcode-space structural map" below).
- Zero or more **extension words** follow, *their count and shape determined by the operand fields of
  the first word*: a `.l` immediate is two extension words; a `d16(An)` mode is one displacement
  extension word; a `d8(An,Xn)` indexed mode is one **brief extension word** (which itself encodes
  the index register, its size, and the 8-bit displacement); `abs.w` is one extension word, `abs.l`
  is two; the 68020 adds **full extension words** with their own sub-fields (out of M4 scope — M4 is
  68000 only, per the ladder).

This is **neither** prefix-based (Z80) **nor** variable-byte-from-the-front (8086). It is
**word-granular with operand-determined extension-word fetch**: you decode the first word, and the
*operand fields tell you how many more words to consume*.

### 3.2 How it maps to the generic multi-byte-key decoder (M3.1b)

This is the most important forward-coupling in the brief, so it deserves care.

**Where the framework is single-byte today (the 6502 wall, soon to be reshaped by M3.1b):**

- `InstructionDef(byte Opcode, …)` — **a single opcode byte** (`InstructionDef.cs:7`).
- The JIT's block discovery reads **one byte** and indexes a **256-slot** table:
  `byte opcode = _bus.Read8(pc); OpcodeDescriptor d = Mos6502Cpu.JitDescriptors[opcode];`
  (`BlockCompiler.cs:80-81`), advancing PC by `d.Length` (`:84`).
- `OpcodeDescriptor.Opcode` is a `byte`, `Length` is "1–3 … discovery advances PC by this"
  (`OpcodeDescriptor.cs:36,41`).
- The importer's opcode-key regex is **`^0x[0-9A-Fa-f]{2}$` — literally two hex digits**
  (`OpcodeDataset.cs:45-46`), so it cannot even *represent* a 16-bit opcode word, let alone
  extension words.

ADR 0001 Decision 1 (`0001-…:98-179`) chose **(A) nested prefix tables + a generated decode walk**
for the Z80, and — this is the load-bearing sentence for M4 — explicitly argued it *generalises
forward*: "option (A) generalizes forward: the 8086's prefix bytes … and **the 68000's word-granular
decode** are both expressible as 'the spec declares its decode structure; the generator emits the
walk'" (`0001-…:173-176`). The checkpoint then accepted "**a generic multi-byte-key decoder**"
(`…framework-design.md:265-266`; `0001-…:660`).

**The honest read for the 68000:** the Z80 decode model (read a byte; if it is a known prefix, switch
tables and read the next byte; for `DDCB` read a displacement *between* prefix and opcode) is a
**byte-stream state machine**. The 68000 is a **word-stream** machine. The M3.1b decoder must
generalise along **two** axes the Z80 alone will not force:

1. **Granularity: the decode unit is a 16-bit big-endian word, not a byte.** If M3.1b builds its
   decode walk and its "multi-byte key" purely in terms of `Read8` byte fetches (because the Z80 is a
   byte machine), the 68000 will need it re-expressed as word fetches. **Recommendation for M3.1b
   (see §"What this means for M3 NOW"): make the decode walk's fetch unit a *parameter* (byte for
   Z80/6502/8086, word for 68000), not a hardcoded `Read8`.** This is cheap to design in now and
   expensive to retrofit.

2. **Operand-determined extension fetch.** The Z80's compound `DDCB dd op` is a *fixed* shape — the
   displacement is always one byte in one position. The 68000's extension-word count is **computed
   from the addressing-mode + size fields of the first word** (a `.l` immediate = +2 words; `abs.l`
   = +2 words; brief-extension index = +1 word). So "how long is this instruction?" is **not a
   constant per opcode** the way `OpcodeDescriptor.Length` is today (`OpcodeDescriptor.cs:41`) — it
   is **a function of the decoded mode + size**. The decoder must compute `Length` from the resolved
   operand fields, and `Discover` must advance PC by that computed length, not a fixed `d.Length`.
   ADR 0001 already loosened this once: "`Discover` reads the **decode function's** total length, not
   a single `d.Length`" (`0001-…:166-168`). M4 generalises it further — the length is **operand-
   computed**, not just prefix-inclusive.

**Does the decoder generalise, or need rework?** *Provisional verdict:* if M3.1b lands the
"spec declares its decode structure; the generator emits a walk that returns `(key, length,
extracted-operand-fields)`" model ADR 0001 chose, **the structure generalises** — the 68000 is "the
walk fetches words and the length is operand-computed." If M3.1b instead lands a Z80-specific
"prefix-table + fixed-displacement" decoder keyed on byte fetches, **the 68000 forces a rework** of
the fetch unit and the length computation. **The difference is entirely in how abstractly M3.1b is
built — which is precisely why this brief is being fed back into M3 now.**

A second 68000-specific decode pressure worth flagging: the encoding is **field-decomposed**, not a
flat table (§5, §map). A 256-slot-per-page array (the Z80 model) is a poor fit for a 16-bit word
where the meaningful key is "the operation bits + the size bits," with the mode/register bits being
*operands*. The 68000 wants the descriptor keyed on the **decoded operation+size**, with mode/register
resolved as operands — a **field-decomposition decoder**, closer to WinUAE's `table68k` than to a
flat opcode array. This is the strongest argument that the M3.1b "multi-byte key" should be an
**opaque key produced by a generated decode function**, not "the concatenated opcode bytes" — because
for the 68000 the key is *derived* from fields, not *equal to* the bytes.

---

## 4. Addressing modes — the richest set yet

### 4.1 What the 68000 needs (the 12+ modes)

| 68000 mode | Syntax | Extension words | EA source |
|---|---|---|---|
| Data register direct | `Dn` | 0 | register (no memory) |
| Address register direct | `An` | 0 | register (no memory) |
| Address register indirect | `(An)` | 0 | `An` |
| …with postincrement | `(An)+` | 0 | `An`, then `An += size` |
| …with predecrement | `-(An)` | 0 | `An -= size`, then `An` |
| …with displacement | `d16(An)` | 1 | `An + sign-extend(d16)` |
| …with index (brief) | `d8(An,Xn.size)` | 1 (brief ext) | `An + Xn + sign-extend(d8)` |
| Absolute short | `(xxx).w` | 1 | sign-extended 16-bit address |
| Absolute long | `(xxx).l` | 2 | 32-bit address |
| PC with displacement | `d16(PC)` | 1 | `PC + sign-extend(d16)` |
| PC with index (brief) | `d8(PC,Xn.size)` | 1 (brief ext) | `PC + Xn + sign-extend(d8)` |
| Immediate | `#xxx` | 1 (`.b/.w`) or 2 (`.l`) | the extension word(s) |

(The 68020 adds memory-indirect and scaled-index full-extension-word modes — **explicitly out of M4
scope**; M4 is 68000, per the ladder `…framework-design.md:274-276`.)

### 4.2 How it maps to our `AddrMode` / class-mode-matrix model

**`AddrMode` is a closed 13-member 6502 enum** (`AddrMode.cs:6-12`), mirrored in three more places:
the JIT's `JitMode` (`OpcodeDescriptor.cs:19-25`, "the same closed set … copied into the JIT data
layer"), the parser's `s_addrModes` (`SpecParser.cs:77-83`), and the importer's
`OpcodeDataset.ValidModes` (`OpcodeDataset.cs:37-43`) + `SpecFileEmitter.SupportedModes`. The Z80
(M3) already forces this set to grow (ADR 0001 Decision 4 table, `0001-…:334-342`); the 68000 grows
it again and **stresses two things the Z80's additions do not**:

1. **The EA source is frequently a register, sometimes with an auto-side-effect.** `(An)`, `(An)+`,
   `-(An)` compute the effective address from an *address register* (like the Z80's `(HL)` —
   `0001-…:336`), but `(An)+`/`-(An)` **mutate the register as a side effect of the access**, and
   the **size of the increment depends on the operation size** (`.b`→1, `.w`→2, `.l`→4, with a
   special case: `(A7)+`/`-(A7)` always move by 2 to keep the stack word-aligned). This is a new EA
   shape: *an addressing mode with a register write-back whose magnitude is the operand size*. Our EA
   computation today (e.g. `EmitReadAbsoluteEa`, the indexed modes in `BlockCompiler.Emit.cs`) is
   pure-functional — it computes an address and never mutates a register. The 68000's
   auto-inc/dec modes are the first EA with a **side effect on architectural state**.

2. **The class/mode legality matrix is far richer and mostly orthogonal.** Our matrix
   (`ValidateModeForClass`, `SpecParser.cs:587-644`) encodes **6502-specific** rules: "register-class
   ops require Implied mode," "Jump requires Absolute or Indirect," "Rmw requires
   ZeroPage/ZeroPageX/Absolute/AbsoluteX/Accumulator." And `RequiredIndexRegister`
   (`SpecParser.cs:580-585`) hardcodes "ZeroPageX/AbsoluteX/IndirectX need a register named `X`" —
   **meaningless for the 68000**, where the index register is *any* `An`/`Dn` named in the brief
   extension word, not a fixed `X`/`Y`. On the 68000, the legal-mode set is largely **a property of
   the instruction's EA-category fields** (data-alterable, memory-alterable, control, etc. — the
   classic 68000 "addressing categories"), applied near-orthogonally across the operation set. ADR
   0001 already predicts this for the Z80: "**Expect the class/mode matrix to be substantially
   rebuilt, not extended**" (`0001-…:376-378`). The 68000 confirms and amplifies it: the matrix
   should become **data-driven from the instruction's EA-category**, not a hand-written per-class
   `switch`. `RequiredIndexRegister`'s "named `X`/`Y`" convention is dead on the 68000.

### 4.3 What the framework must grow

- `AddrMode`/`JitMode`/`s_addrModes`/`OpcodeDataset.ValidModes`/`SpecFileEmitter.SupportedModes`
  gain the 12 modes (the **fourth** mirror-edit each addition costs today — a smell §"M3 NOW"
  addresses).
- A new EA capability: **register write-back with operand-size magnitude** (`(An)+`/`-(An)`).
- The class/mode matrix moves from 6502 hardcoded rules toward **EA-category-driven** legality;
  `RequiredIndexRegister`'s fixed-name convention is retired/generalised (the index register is an
  operand, not a fixed register).
- **PC-relative modes** (`d16(PC)`, `d8(PC,Xn)`) — the EA depends on PC at the *instruction's*
  address; in the JIT these are compile-time-resolvable (PC is known at emit time) — a small win for
  the optimizer, like the 6502 branch targets.

---

## 5. Instruction set scale & regularity — the field-encoded ISA

### 5.1 What the 68000 needs (the regularity, and why it matters)

The 68000 ISA is **large** (provisional count §map) but **strikingly regular**: an instruction is
`operation × size × addressing-mode × register`, packed into the 16-bit word's fields. `ADD`,
`SUB`, `AND`, `OR`, `MOVE`, `CMP` etc. are each *one operation* that fans out across all legal sizes
× modes × registers. This is the opposite of the 6502/Z80 "flat-ish opcode table where each byte is
a near-arbitrary (mnemonic, mode) pair."

### 5.2 How the importer dataset schema + DSL micro-op vocabulary handle a field-encoded ISA

**The importer is built for a flat opcode table — and says so.** `OpcodeDataset` is one row per
opcode with a `bytes`/`mode`/`cycles` triple (`OpcodeDataset.cs:12-19`), keyed by a **single-byte**
opcode string (`:45`), with **6502 byte-count rules** (`ExpectedBytes`, `:146-153`) and the **13
6502 modes** (`:37-43`). The semantics map is **one entry per mnemonic** (`SemanticsMap` +
`extraction-runbook.md:70`), which is *good* for a regular ISA (one `ADD` entry covers all sizes ×
modes). But:

- The single-byte opcode key (`OpcodeDataset.cs:45`) cannot represent a 16-bit instruction word.
- `ExpectedBytes` (`:146-153`) bakes 6502 length-from-mode rules; the 68000's length is
  **operand-computed** (§3) — `bytes` is not a constant per row.
- The mode vocabulary and the factory list (`SemanticsMap.FactoryArity`, `:44-82`) are 6502.

**The structural insight the brief asks for: a field-decomposition might beat a flat opcode table for
the 68000.** Because the 68000 is field-encoded, the natural dataset is **not** "enumerate all ~64K
word values" (most are illegal; a flat 64K table is ~the wasteful (C) option ADR 0001 rejected for
the Z80, `0001-…:144-150`). It is **"declare the field grammar once per operation"**: an entry says
"`ADD` occupies opcode bits `1101 rrr ooo eeeeee`, legal sizes B/W/L, EA category data-alterable,"
and the generator *expands* the legal (size × mode × register) combinations. This is closer to
WinUAE's `table68k` (research §2, `…research.md:96-98` cites it as the model) than to
`mos6502-opcodes.json`. **Recommendation (provisional):** the M4 importer schema should grow a
**field-pattern** representation for regular ISAs (operation bit-pattern + size field + EA-category)
alongside the flat per-opcode rows, and the generator should expand patterns into descriptors. This
is a larger schema change than the Z80's (which is still a flat-ish prefixed table) — flag it as M4
extraction work, not free.

### 5.3 New micro-ops the 68000 needs

Extending `Op.cs` / `Spec.cs` / `s_microOpSignatures` (`SpecParser.cs:38-75`) / `FactoryArity`
(`SemanticsMap.cs:44-82`), and the JIT's `JitOp` kind strings + `BlockCompiler` emit arms. The
68000's vocabulary is *broad* but mostly **size-parameterised** versions of familiar ops:

- **Size-parameterised data movement & ALU:** `Move(size)` (the workhorse — `MOVE` is the most
  common 68000 instruction and sets CCR), `Add/Sub/And/Or/Eor/Cmp(size)`, `Adda/Suba/Cmpa/Movea`
  (address-register forms — **no CCR**, `.w` sign-extends), `Addq/Subq` (quick immediate, 3-bit),
  `Addi/Subi/Andi/Ori/Eori/Cmpi` (immediate), `Addx/Subx/Negx` (extend-using, the **`X` flag**
  consumers), `Neg/Not/Clr/Tst`. The size axis means each of these is one micro-op with a `Size`
  operand, *not* three micro-ops.
- **Multiply/divide:** `Mulu/Muls` (16×16→32), `Divu/Divs` (32÷16→16:16) — these set CCR including
  `V` on overflow, and **divide-by-zero raises an exception** (§6). New to our vocabulary entirely
  (the 6502/Z80 have no hardware mul/div).
- **Shift/rotate family (size-parameterised, register-count or immediate):** `Asl/Asr/Lsl/Lsr/
  Rol/Ror/Roxl/Roxr` — the `Rox` forms rotate **through the `X` flag** (the second carry). Memory
  forms shift by 1; register forms shift by a count.
- **Bit ops:** `Btst/Bset/Bclr/Bchg` — a **bit-number operand** (immediate or in a `Dn`), the same
  "sub-operand the 6502 has no analog for" that ADR 0001 flags for the Z80's `BIT n` and warns the
  fixed `JitOp` shape cannot carry (`0001-…:351-353, 396-398`, J10). The 68000 makes this
  non-optional.
- **BCD:** `Abcd/Sbcd/Nbcd` — packed BCD using the `X` flag (the 68000 has no decimal-mode flag like
  the 6502 `D`; BCD is explicit instructions, **like the Z80's `DAA` posture** — ADR 0001 J7,
  `0001-…:511`, already concludes "decimal handling must be per-CPU, a spec-declared capability").
- **The genuinely-68000 movers:** `Movem` (move multiple registers to/from memory via a register-mask
  word — a *one-instruction loop* over a bitmask, stressing the block model like the Z80 block ops,
  ADR 0001 `0001-…:358-361`); `Movep` (move peripheral data, byte-lane-strided — for 8-bit
  peripherals on a 16-bit bus); `Exg` (exchange two registers); `Swap` (swap the two words of a
  `Dn`); `Lea` (load effective address — computes an EA into an `An` with no memory access — a
  *pure EA op*, novel); `Pea` (push EA); `Link/Unlk` (stack-frame setup); `Ext` (sign-extend
  `.b`→`.w`→`.l` within a `Dn`).
- **Control flow:** `Bcc` (16 conditions, byte or word displacement — richer than the 6502's 8
  branches), `Bra/Bsr` (relative), `Jmp/Jsr` (via any control-mode EA — so the target can be
  *register-indirect*, a **dynamic** successor the JIT cannot chain, like Z80 `RET`/6502 RTS, ADR
  0001 J9 `0001-…:513`), `Dbcc` (decrement-and-branch — the 68000's loop primitive, the hottest
  chainable backward edge, exactly analogous to the Z80 `DJNZ` ADR 0001 calls "the hottest loop
  primitive," `0001-…:513`), `Rts/Rtr/Rte` (return / return-and-restore-CCR / return-from-exception),
  `Scc` (set byte on condition), `Tst`.
- **Privileged / system:** `Trap`/`Trapv`/`Chk` (exception-raising), `Reset`, `Stop` (a halted state —
  like the Z80 `HALT`, ADR 0001 J6 `0001-…:510`, needs the `Run` loop to not busy-spin), `Move SR`/
  `Move CCR`/`Move USP`/`Andi/Ori/Eori to SR/CCR` (the privileged ones raise a **privilege violation**
  in user mode — §6).

**What stresses the generator's class/mode matrix vs. what the JIT has never seen** (the ADR 0001
Decision 4 framing, `0001-…:371-385`):

- *Generator class/mode matrix* — **rebuilt, not extended** (already the Z80 verdict; the 68000's
  near-orthogonal size × EA-category fan-out makes a hand-written per-class `switch` untenable). The
  matrix wants to be data-driven from EA-category + size-legality, computed from the field grammar.
- *JIT emit loop* — **has never seen:** 32-bit register math in IL (§8); the `X`-flag chains
  (`Addx`/`Roxl`/BCD); operand-size-parameterised arithmetic (one arm, three widths); the bit-number
  operand (J10); `Movem`'s mask loop; `Lea`'s pure-EA computation; mul/div (and div-by-zero
  exception); the big-endian wide memory access (§2/§8). Most of these are reasonable **fallback**
  candidates first (the proven `NeedsFallback` valve, `OpcodeDescriptor.cs:43`,
  `BlockCompiler.cs:137`), with the hot straight-line ones (`MOVE`, `ADD`/`SUB`, `Bcc`/`Dbcc`)
  promoted to emitted IL — the same staged approach ADR 0001 Decision 4 mandates (`0001-…:386-391`).

---

## 6. Exceptions / interrupts

### 6.1 What the 68000 needs

- **256-entry exception vector table at address `0`** (vectors 0–255, each a 32-bit pointer): reset
  (vectors 0/1 = initial SSP + initial PC), bus error (2), address error (3), illegal instruction
  (4), divide-by-zero (5), CHK (6), TRAPV (7), privilege violation (8), trace (9), line-A/line-F
  emulator traps (10/11), the **autovector interrupts** (25–31), the **TRAP #0–#15** software traps
  (32–47), and the user/device vectors (64–255).
- **7 prioritized interrupt levels** via `IPL0–IPL2` pins; the `SR` 3-bit interrupt **mask** gates
  them (an interrupt at level ≤ mask is held off; **level 7 is non-maskable**, edge-triggered). On
  acknowledge, the device either supplies a **vector number** (vectored interrupt) or signals
  **autovector** (the CPU uses vectors 25–31 by level).
- **Supervisor/user mode.** Exceptions and interrupts **switch to supervisor mode**, push a frame
  (PC + SR, and for bus/address error a larger frame with access info) onto the **supervisor** stack
  (SSP), and vector through the table. `RTE` restores SR (and thus mode) + PC.
- **Address error** (odd-address word/long access, §2) and **bus error** (`BERR` pin) are
  *synchronous* faults with extended stack frames.

### 6.2 vs. our 6502/Z80 fixed-vector + IRQ/NMI model, and how the interrupt seam generalizes

**This is the seam ADR 0001 identifies as *already generic* — the positive proof point** (Decision 5,
`0001-…:402-447`, "the one place the framework is **already** generic"). The mechanism: the generated
`Step` calls a `partial bool TryServiceInterrupt()` before the opcode fetch and exposes a
`partial bool InterruptPending`, and the **per-CPU hand-written partial implements the policy**
(`Mos6502Cpu.cs` for the 6502; the Z80 partial for `IM 0/1/2`+NMI+IFF). The JIT samples
`InterruptPending` at block boundaries / chain edges (`JittedCpu.cs:90`, `BlockCompiler.cs:496-499`)
**without knowing the policy** — it just asks the CPU.

**Does it generalise to the 68000?** *Provisional verdict: the seam's shape survives, but the 68000
adds two things the 6502/Z80 partials never needed, and one is a real wiring question:*

1. **A priority/mask comparison instead of a single I-flag gate.** The 6502's `InterruptPending` is
   `_nmiPending || (_irqLine && I-clear)`. The 68000's is "**incoming IPL level > SR mask**" (or
   level 7). That is still expressible inside `InterruptPending`/`TryServiceInterrupt` — it is *more
   logic in the partial*, not a seam change. The `IInterruptLine` wired-OR (ADR 0001 reuses it for
   the Z80, `0001-…:438`) carries a boolean line today; the 68000 needs a **3-bit level**, not a
   bool. **This is the one likely contract nudge:** either a level-carrying interrupt line, or the
   machine encodes level into which of several lines is asserted. Flag as an enumerated M4 finding.
2. **Mode switching + a supervisor stack + a 256-entry data-driven vector table.** The service
   sequence (switch to supervisor, push frame to SSP, vector through `mem[vector*4]`) is **per-CPU
   policy in the partial** — exactly where the 6502's fixed-7-cycle `$FFFE` sequence lives today
   (`Mos6502Cpu.cs`). The 68000's is longer and reads the vector from low memory, but it is the same
   altitude. **The framework change is small** (the partial does more); the seam holds. ADR 0001's
   prediction — "if anything here needs a `Core` change, that is a finding; if nothing does, that is
   the proof point" (`0001-…:443-444`) — applies: the 68000 likely needs only the **interrupt-line
   level** nudge, nothing deeper.
3. **`STOP` (halted state)** needs the `Run` loop / dispatcher to not busy-spin — the **same** issue
   ADR 0001 raises for the Z80 `HALT` (J6, `0001-…:510`; open question 8, `0001-…:720-723`). If M3.2
   handles `HALT` for the Z80, the 68000 `STOP` reuses it. **This is a direct M3→M4 reuse — call it
   out in M3.2.**

**The new dimension the 68000 genuinely adds: synchronous CPU-raised exceptions (address error,
illegal instruction, div-by-zero, TRAP, privilege violation, CHK, TRAPV).** These are *not*
asynchronous interrupt lines — they are raised **mid-instruction by the CPU itself** and must vector
just like an interrupt. The interpreter handles these naturally (a micro-op detects the condition and
invokes the same vector machinery). **The JIT must treat an exception-raising op as block-ending /
fallback** (an emitted `DIVU` that might divide by zero, or any word access that might be
misaligned, must be able to bail to the exception path) — analogous to how `BRK` is `NeedsFallback`
+ `EndsBlock` today (`OpcodeDescriptor.cs:50-53`). This is a **new flavour of block exit** (a
*conditional, mid-instruction* vector) the JIT's current exit set (Normal/Budget/Recompile, plus the
interrupt boundary sample) does not have. Flag as an M4 JIT design item.

---

## 7. Validation — TomHarte m68000 + the cycle-accuracy question

### 7.1 Availability and what it checks

The SingleStepTests **`m68000`** vector set exists and is cited in the research doc (§8,
`…research.md:234,276`, `https://github.com/SingleStepTests/m68000`) and the design spec basis. It
is the same shape as the 6502/Z80 sets the framework already consumes through the generic TomHarte
harness (design spec §8, `…framework-design.md:177-184`): per-opcode JSON, initial state → one
instruction → **final state + per-cycle bus trace** diffed against the recording bus.

The harness is **CPU-agnostic by construction** — it drives any `ICpuCore` via the introspection
interface (`GetRegister`/`SetRegister`/`RegisterNames`, which the data-driven register file from
M3.1a makes work for the 68000's register names for free). So the *harness* needs no 68000-specific
work; the **vector ingestion** needs to handle the 68000's state shape (the 16 registers + SR + PC,
and the bus trace's **word-sized** transactions and **byte order** — which is why §2's wide-bus
decision matters for the *recording bus* too: the trace records word/long accesses).

### 7.2 The cycle-accuracy question — flag it loudly for M4

ADR 0001 already raised this as an open question for the Z80 (open question 5, `0001-…:704-707`): the
6502's "one micro-op = one cycle" made the interpreter cycle-true cheaply (design spec §6); the Z80's
M-cycle/T-state model does not map as cleanly. **The 68000 is worse.** Its timing is genuinely
complex:

- Instruction timing is **not** one-cycle-per-bus-access. The 68000 has internal cycles, prefetch
  overlap, and per-instruction timing tables (the classic "MOVE.L (A0)+,(A1)+ = 20 cycles" tables),
  plus the **two-word-cycles-per-long** bus behaviour (§2).
- The JIT's cycle model today is `BaseCycles` per descriptor + a `PageCrossPenalty` bool
  (`OpcodeDescriptor.cs:42-43`) — ADR 0001 J5 (`0001-…:509`) already says `BaseCycles`/page-cross is
  6502-specific and "the descriptor must carry the instruction's total count" for the Z80. The 68000
  pushes this further: the cycle count is **a function of the addressing mode + size + (for some ops)
  the operands** (e.g. shift count, `MOVEM` register count, taken/not-taken branch), not a constant.

**Open question for M4 (provisional recommendation):** hold the **interpreter** to TomHarte's
per-cycle bus-trace fidelity (it is the correctness oracle and the vectors demand it), but be
**explicit** that the cycle *count* per instruction is computed from a 68000 timing model (mode +
size + operands), not a constant `BaseCycles`. The `PageCrossPenalty` field is 6502-only and should
generalise to "a per-arch timing addend" (ADR 0001 J5's exact prediction, `0001-…:509`). **Do not
over-fit the 6502/Z80 cycle model in M3** (see §"M3 NOW").

---

## 8. JIT genericity implications — which 6502-isms the 68000 specifically stresses

ADR 0001 Decision 7 (`0001-…:497-528`) enumerates the JIT's recorded 6502-isms (J1–J10) and how the
Z80 reshapes each. This section asks the brief's question: **which of those does the 68000 stress
that the Z80 does NOT**, plus the new ones. This is the part that most determines whether the post-M6
optimization is truly arch-valid, because the optimizer reasons about exactly these.

| Audit row | What the Z80 already reshapes | What the 68000 stresses *beyond* the Z80 |
|---|---|---|
| **J1** — `BlockCompiler`/`BlockDelegate` typed to `Mos6502Cpu` (`BlockCompiler.cs:16,69,97`; `JittedCpu.cs:19,64`) | The Z80 forces the compiler generic over the CPU type (ADR 0001 defers this to M3.5). | **Confirms it must be data, not a third concrete type.** By M4 the compiler must be generic over *N* CPU types; if J1 is solved Z80-shaped (a `Mos6502Cpu`-or-`Z80Cpu` union), the 68000 breaks it again. The 68000 is the third type that proves J1 is genuinely generic. |
| **J2** — six baked `FieldInfo`s `FA/FX/FY/FS/FP/FPC` + `RegField(byte)` over indices 0–3 (`BlockCompiler.cs:37-42,454-458`; emit sites `BlockCompiler.Emit.cs:269,288,467,475`) | M3.1a makes the register file **data** (resolve `FieldInfo` by name). **NOTE: the source shows J2 is NOT yet landed in the JIT** — `JitOp` still carries `byte RegA/RegB` (`OpcodeDescriptor.cs:32`) and `RegField` still switches indices 0–3 (`BlockCompiler.cs:454-458`). M3.1a Task 4 (the JIT half) is pending. | **The 68000 needs ~17 register fields (`D0–D7`,`A0–A7`,`SR`,`PC`) — but more importantly, *wider* ones (32-bit).** J2-by-name (M3.1a) handles the *count/names*; the 68000 adds that the field **type is `uint`, not `byte`** — see J-new-A below. The register-hoisting optimization (design spec §6) must allocate 32-bit locals, not byte locals. |
| **J3** — `JitDescriptors[opcode]` single-byte 256-slot index; `Discover` advances by `d.Length` (`BlockCompiler.cs:80-84`) | M3.1b: per-page tables + a generated decode walk; length is decode-function-computed. | **Word-granular fetch + operand-computed length (§3).** The Z80 walk is byte-fetch + fixed displacement; the 68000 is **word-fetch + length computed from mode/size fields**. If M3.1b's decode walk hardcodes `Read8`, the 68000 reworks it. |
| **J4** — `LoadByteFromBus`/`EmitStoreByte` byte-only fastmem (`BlockCompiler.cs:277-419`) | The Z80 reuses byte fastmem unchanged (16-bit ops = two byte accesses) — ADR 0001 calls J4 "genuinely generic" (`0001-…:508`). | **THE big one. The 68000 is the first true wide + big-endian bus access (§2).** The Z80 *confirms* byte fastmem; the 68000 *extends* it to a wide, byte-ordered load/store. This is the fastmem dimension the Z80 leaves untested (ADR 0001 verdict, `0001-…:631-634`). Either wide bus primitives (§2 option A) or two-byte composition (option B) — the emit arms grow either way. |
| **J5** — `BaseCycles` + `PageCrossPenalty` cycle model (`OpcodeDescriptor.cs:42-43`; `BlockCompiler.cs:223`) | The Z80 needs total-T-state-in-descriptor; loosens the per-access charge. | **Operand-dependent cycle counts (§7)** — `MOVEM` register count, shift count, taken-branch, two-cycles-per-long. The Z80 makes cycles per-instruction-variable; the 68000 makes them **operand-variable**. `PageCrossPenalty` is doubly dead (no 6502-style page-cross *and* the timing is operand-driven). |
| **J6** — interrupt boundary sample (`JittedCpu.cs:90`, `BlockCompiler.cs:496`) | Survives; Z80 `HALT` needs no-busy-spin. | **Survives, plus `STOP` (reuse the Z80 `HALT` handling) AND a new exit flavour: mid-instruction synchronous exceptions (§6)** — address error / div-by-zero / TRAP must be able to bail an emitted op to the vector path. New conditional block-exit. |
| **J7** — decimal arm is 6502 NMOS BCD verbatim (`BlockCompiler.Decimal.cs`) | Dead for the Z80 (`DAA` is a different arm); confirms decimal is per-CPU/spec-declared (`0001-…:511`). | **Also dead for the 68000** (BCD is `ABCD/SBCD/NBCD` using `X`). *Re-confirms* the J7 finding from a second angle — decimal is a spec-declared capability, not a fixed JIT feature. Good corroboration. |
| **J8** — 256-byte-page SMC/dirty bitmap (`BlockCompiler.cs:186-209`; `Fastmem.cs`) | Z80 confirms it generic (runs code from RAM; larger instrs span pages harder, `0001-…:512`). | **Survives.** 68000 code runs from RAM too; pages stay 256-byte (`AddressSpace.cs:10`). The 68000's larger instructions (up to ~10 bytes with two long extension words) span pages even harder than the Z80 — exercises `PagesSpanned` (`BlockCompiler.cs:123-131`) hardest yet, but no shape change. Wide writes mark the page dirty the same way. |
| **J9** — block-ending classification (`OpcodeDescriptor.EndsBlock`; chainable-vs-dynamic in `BlockCompiler.Flow.cs`) | Z80 adds conditional CALL/RET/JR/DJNZ/RST/block-ops/HALT; `DJNZ` is the hot chainable backward edge (`0001-…:513`). | **`Dbcc` is the 68000 `DJNZ`** (hot chainable backward edge — same optimizer pressure). `Bcc`/`Bsr`/`Bra` are static (chainable like 6502 branches/JSR). `Jmp`/`Jsr` **via register-indirect EA** and `Rts`/`Rte` are **dynamic** (not chainable — like RTS/RET). `Movem` is a one-instruction loop (like Z80 block ops). So J9 is stressed *similarly* to the Z80 — good, it means the chain model is exercised by two arches the same way. |
| **J10** — `JitOp` operand shape `(RegA,RegB,FlagBit,BoolArg)` (`OpcodeDescriptor.cs:32`) | Z80 needs a bit-index slot + 16-bit immediates; ADR 0001 says it needs an extensible operand model (`0001-…:396-398`). | **The 68000 needs a `Size` operand, a bit-number operand (`Btst` etc.), a register-*mask* operand (`Movem`), and shift counts** — the fixed 4-field `JitOp` is even less adequate. Strongly corroborates ADR 0001's "extensible operand model" — and the **`Size` field is the new universal one** (almost every 68000 op carries it). |

**New JIT 6502-isms the 68000 surfaces that the audit did not name (because neither 6502 nor Z80 hit
them):**

- **J-new-A — every emitted register access is byte-typed (`Conv_U1`).** `BlockCompiler.Emit.cs` is
  saturated with `OpCodes.Conv_U1` (e.g. `:275,478,489,517,576`) and the flag math masks to bytes
  (`BlockCompiler.cs:431,447`, `:431` `0x7D`). The Z80's 16-bit ops add `ushort`/`Conv_U2`; the
  **68000 adds 32-bit `uint`/no-truncation, and the *partial-write* of `.b`/`.w` into a 32-bit
  register** (read-modify-write the low byte/word, preserve the upper bits) — emitted IL it has never
  produced. This is the ">16-bit registers in emitted IL" the brief flags. The register-hoisting
  optimization must hoist 32-bit locals.
- **J-new-B — `EaLocal`/`AddrLocal` are `uint` flat addresses.** `EmitContext` uses a `uint` `EaLocal`
  (ADR 0001 `0001-…:624` notes `EaLocal is uint`). The 68000 is **24-bit flat** — *fits a `uint`
  fine* (this is the dimension the 8086 stresses, not the 68000). **Positive note: the 68000 does
  NOT stress the flat-address assumption** (it is flat, just big-endian and word-accessed). The 8086
  (M5) is where `uint`-flat breaks; the 68000 leaves it intact, which is the correct division of
  labour per the ladder.
- **J-new-C — the `X` flag is a second carry the flag IL has no slot for.** `Addx`/`Roxl`/BCD chain
  through `X`; the emitted flag math (`EmitSetCarry`, `BlockCompiler.Flow.cs:292-306`) knows one carry
  (`P & 1`). The flag vocabulary must carry `X` distinctly — ties to §6 and the M3 flag-model work.

**Bottom line for the optimization (M6).** ADR 0001 says J1+J2+J3 (generic CPU type, register file,
decode) are "the single most important outcome of M3 for the optimization goal" (`0001-…:522-528`).
The 68000's contribution is to prove the **memory/data half**: **J4 (wide big-endian bus), J-new-A
(32-bit register IL), J5 (operand-dependent cycles)**. These are the dimensions the Z80 leaves
untested (ADR 0001 verdict). So the M3→M4→M5→M6 ladder is well-chosen: Z80 = front half
(decode/register-count/flag/block), **68000 = data/memory half (width + endianness + wide bus +
operand-cycles)**, 8086 = addressing half (segmentation + `uint`-flat break + variable-byte decode).
Only after all three is register allocation / block linking / inlining provably arch-valid.

---

## Opcode-space structural map

> **PROVISIONAL — STRUCTURAL-ONLY — UNVERIFIED-PENDING-M4-EXTRACTION.** This is the encoding
> *grammar* and operation *groups*, not a byte-accurate table. M4's extraction job (cross-source
> diff of two independent references + the TomHarte m68000 vectors) is what produces the verified
> table. Counts below are **estimates** to size the work, not facts.

### The first-word encoding grammar

The 68000 groups instructions by the **top 4 bits** of the 16-bit instruction word (the classic
"line" decomposition — each "line" is one hex digit of the high nibble):

```
 bits: 15 14 13 12 | 11 10 9 | 8 7 6 | 5 4 3 | 2 1 0
       └─ line ───┘  └─ reg ─┘ └─op/sz┘ └ mode┘ └ reg ┘    (typical dual-operand shape)
                                        └──── EA (mode:reg, 6 bits) ────┘
```

The recurring sub-fields:
- **EA = 6 bits = `mode(3) : register(3)`** — the addressing mode and its register, near-uniform
  across the ISA. Mode `111` is an escape: register sub-field then selects abs.w / abs.l / d16(PC) /
  d8(PC,Xn) / immediate.
- **Size = 2 bits** (`00`=byte, `01`=word, `10`=long) for the size-bearing ops (a *different* 2-bit
  encoding appears in MOVE, which packs size in bits 13–12).
- **Condition = 4 bits** for `Bcc`/`Scc`/`Dbcc` (16 conditions).

### Major operation groups (by line / high nibble) — provisional

| Line (hi nibble) | Operation group | Notes |
|---|---|---|
| `0000` | Immediate ops + bit ops + MOVEP | ORI/ANDI/SUBI/ADDI/EORI/CMPI (incl. *to CCR/SR*), BTST/BSET/BCLR/BCHG, MOVEP |
| `0001/0010/0011` | **MOVE.b / MOVE.l / MOVE.w** | the workhorse; size in bits 13–12; also MOVEA (dest = An) |
| `0100` | Miscellaneous | LEA, PEA, MOVEM, CHK, CLR, NEG/NEGX, NOT, TST, EXT, SWAP, NBCD, TRAP, LINK/UNLK, MOVE USP, RESET/NOP/STOP/RTE/RTS/RTR/JMP/JSR |
| `0101` | ADDQ/SUBQ + Scc + DBcc | quick immediates; set-on-cc; decrement-branch |
| `0110` | **Bcc / BRA / BSR** | relative branches (8/16-bit displacement) |
| `0111` | MOVEQ | move quick (sign-extended 8-bit immediate → Dn.l) |
| `1000` | OR / DIV / SBCD | OR, DIVU/DIVS, SBCD |
| `1001` | SUB / SUBX / SUBA | |
| `1011` | CMP / EOR / CMPA / CMPM | |
| `1100` | AND / MUL / ABCD / EXG | AND, MULU/MULS, ABCD, EXG |
| `1101` | ADD / ADDX / ADDA | |
| `1110` | Shift / rotate | ASL/ASR/LSL/LSR/ROL/ROR/ROXL/ROXR (register & memory forms) |
| `1010` | Line-A (unimplemented → exception) | reserved emulator trap |
| `1111` | Line-F (coprocessor / unimplemented) | reserved (FPU on later parts) |

### Count estimate (provisional)

The 68000 has on the order of **~80–90 distinct mnemonics**. Counting size variants and the EA fan-out
expands the *encoded* space to **several thousand legal instruction-word values** (most heavily from
MOVE and the dual-operand ALU ops × sizes × EA combinations). The vast majority of the 64K word space
is **illegal** (→ illegal-instruction exception). **This is the structural argument for the
field-decomposition dataset (§5.2):** enumerating a flat table is wrong; declaring the field grammar
per operation and expanding the legal (size × EA × register) combinations is the right shape — and is
why the M3.1b "multi-byte key" should be an **opaque key from a generated decode function**, not "the
opcode bytes."

*(All counts above are estimates for work-sizing. The verified table is M4's extraction deliverable.)*

---

## What this means for M3 NOW

Concrete, actionable constraints for the **in-flight** M3 generalizations, so M4 is cheap and the
abstractions do not bake in a Z80/6502 bias. Each maps to a current M3 chunk.

**For M3.1a (data-driven register file — landing now; source shows Op records already string, JIT
half pending):**

1. **Land J2 in the JIT (Task 4) resolving fields BY NAME, and design the name→FieldInfo map to be
   width-agnostic.** The source shows `JitOp` still carries `byte RegA/RegB` and `RegField` still
   switches indices 0–3 (`OpcodeDescriptor.cs:32`, `BlockCompiler.cs:454-458`). When Task 4 makes
   these name-keyed, **do not bake the field *type* as `byte`** in the resolution — the 68000 needs
   32-bit fields, the Z80 16-bit. The map should resolve `FieldInfo` (whose `.FieldType` carries the
   width) and the emit arms should consult that type rather than hardcoding `Conv_U1`. *Cheap to
   design in now; J-new-A is expensive to retrofit.*
2. **When relaxing the register model, make `RegisterDef.Bits` validation a `>= 8 && <= 32, power-of-two`
   check, not "8 or 16."** M3.1a need not *implement* 32-bit math (out of scope, correctly), but the
   *validation* and the field-typing path (`byte`/`ushort`/**`uint`**) should not be written in a way
   that assumes ≤16. The M3.1a plan already scopes 16-bit *math* out (Ground truth C); just make sure
   the *width plumbing* (field type selection) is a clean function of `Bits`, not a two-case switch.

**For M3.1b (generic multi-byte-key decoder — next):**

3. **Make the decode walk's *fetch unit* a parameter, not a hardcoded `Read8`.** The Z80 is a
   byte-stream machine; building the decoder purely on byte fetches is the natural Z80 implementation
   and the 68000-forcing trap. The 68000 fetches **16-bit big-endian words**. Parameterise the fetch
   granularity (byte vs word) in the generated decode walk. *(§3.)*
4. **Make the "multi-byte key" an *opaque key produced by the decode function*, not "the concatenated
   opcode bytes."** For the 68000 the descriptor key is *derived from fields* (operation + size),
   with mode/register as *operands* — it is not equal to the instruction bytes. If M3.1b's key is
   literally "the bytes," the 68000 needs a different keying scheme. Design the key as "whatever the
   generated decode function returns," which the Z80's prefix-table model also satisfies. *(§3, §5.2.)*
5. **Make `Length` operand-computed, not a fixed per-descriptor constant.** ADR 0001 already loosens
   `Discover` to use the decode function's total length (`0001-…:166-168`). Go one step further in the
   *contract*: the length is a function of the decoded mode/size (the 68000's extension-word count),
   not a constant on the descriptor. The Z80's fixed-shape prefixes don't force this, but designing
   `Length` as "what the decode walk computed" (rather than `OpcodeDescriptor.Length` as a constant
   field) costs little now. *(§3.)*
6. **Plan the class/mode matrix as data-driven (EA-category), not a hand-written per-class `switch`,
   and retire `RequiredIndexRegister`'s fixed-name convention.** `ValidateModeForClass`
   (`SpecParser.cs:587-644`) and `RequiredIndexRegister` (`:580-585`) are 6502 hardcoded; ADR 0001
   already says the matrix is "rebuilt, not extended" for the Z80 (`0001-…:376-378`). When M3.1b
   rebuilds it, structure legality as **EA-category/mode-set data** (the index register is an
   operand, not a register named `X`/`Y`) so the 68000's near-orthogonal size × EA fan-out drops in.
   *(§4.2.)*
7. **Give `JitOp` an extensible operand model now, and add a `Size` slot.** ADR 0001 J10 already calls
   for this for the Z80's bit-index/16-bit-immediate (`0001-…:396-398`). The 68000 needs `Size` on
   nearly every op, plus bit-number / register-mask / shift-count. The fixed
   `(RegA,RegB,FlagBit,BoolArg)` shape (`OpcodeDescriptor.cs:32`, now strings after M3.1a) should
   become extensible. **`Size` is the highest-value addition** — it is universal to the 68000 and
   absent from 6502/Z80. *(§5.3, §8 J10.)*

**For M3.2 (bus + interrupt seams):**

8. **Design the wide-bus question explicitly, even though M3/Z80 only needs `Read8`.** The Z80's
   16-bit ops decompose into two byte accesses (ADR 0001 J4), so M3 *can* ship byte-only. But the
   §2 decision — add `Read16/Read32/Write16/Write32` to `IAddressSpace` with endianness as a bus
   property — is the central M4 seam, and the `IPeripheral` width is *already* in the contract
   (`AddressSpace.cs:83`). **M3.2 should at minimum not foreclose it:** keep the bus's transaction
   surface clean and additive, and **record `Endianness`/wide-access as a known M4 contract growth**
   rather than letting M3 cement "byte-only little-endian-composed" as an unstated invariant. *(§2.)*
9. **When M3.2 handles the Z80 `HALT` no-busy-spin in `Run`, build it as a generic "halted state,"
   not a Z80-specific flag** — the 68000 `STOP` reuses it verbatim (§6.3, ADR 0001 J6/open-q 8).
10. **Note the interrupt-line *level* nudge for M4.** `IInterruptLine` carries a bool today; the 68000
    needs a 3-bit IPL level. M3.2 need not implement it (the Z80's `INT`/NMI are boolean), but
    **record it as the one likely interrupt-seam contract growth M4 forces** (§6.2) so it is a
    planned finding, not a surprise.

**Cross-cutting (the mirror-table smell):**

11. **Every addressing-mode/flag/op addition still costs 3–4 synchronized edits** (`AddrMode` +
    `JitMode` + `s_addrModes` + `OpcodeDataset.ValidModes`/`SpecFileEmitter.SupportedModes`; flags
    in `Flag` + `s_flagMembers` + `CpuEmitter.FlagBit`). M3.1a kills this for *registers*; the same
    "make it data, not mirrored enums" treatment is the right end-state for **modes and flags** too.
    The 68000 (12 modes, the `X` flag, the size axis) is the second arch to pay this tax — strong
    evidence to schedule the **flag-model data-driven** work (ADR 0001 Decision 3/4, scoped *out* of
    M3.1a) so it is done before M4 rather than mirror-edited twice.

---

## M4 Risk List & Open Questions

1. **Wide-bus contract change (§2) — the load-bearing M4 decision.** Add `Read16/32`/`Write16/32`
   with endianness to `IAddressSpace` (option A, recommended), or compose from `Read8` with a
   per-CPU big-endian policy (option B)? Option A is what *proves* the bus abstraction is generic
   (the M4 thesis) and gives a natural home for the address-error check and cycle fidelity; option B
   is cheaper but dodges the proof. **Risk:** if option B is chosen for expedience, M4 ships with the
   bus still secretly 8-bit/little-endian — the 6502-shaped trap one layer down. **Owner sign-off
   needed**, mirroring ADR 0001's open-question posture.

2. **The `Size` axis is a genuinely new model dimension (§1, §5).** Width as a property of
   (instruction × micro-op), with **partial-write** (data reg `.b`/`.w`) vs **whole-write-sign-extend**
   (`An.w`) semantics, is the single largest *semantic* growth — larger than any single Z80 quirk.
   **Risk:** under-modelling it (e.g. treating `.w` as a whole-register write) silently corrupts the
   upper bits — a class of bug TomHarte will catch but only after the design is wrong. **Open:** the
   width-tagged-micro-op design (§1.2 option A) needs validation against the actual MOVE/MOVEA
   encodings during extraction.

3. **Cycle accuracy bar (§7).** The 68000's timing is operand-dependent (MOVEM count, shift count,
   two-cycles-per-long, taken branches), worse than the Z80's M-cycle/T-state model. **Open
   (extends ADR 0001 open-q 5):** hold the interpreter to TomHarte per-cycle bus-trace fidelity (it
   is the oracle), but the cycle *count* must be a 68000 timing model, not a constant `BaseCycles`.
   Confirm `PageCrossPenalty` generalises to a per-arch timing addend (J5) rather than being special-
   cased. **Risk:** the JIT's block cycle-budget (`OpcodeDescriptor.BaseCycles`,
   `BlockCompiler.cs:223`) needs operand-aware cycle costs — more than a constant per descriptor.

4. **Synchronous mid-instruction exceptions (§6) — a new JIT block-exit flavour.** Address error
   (odd-address word access), div-by-zero, TRAP, privilege violation, CHK, TRAPV are raised
   *mid-instruction* and must vector. **Risk:** the JIT's exit set (Normal/Budget/Recompile +
   interrupt boundary) has no "conditional mid-instruction vector." **Open:** are exception-capable
   ops `NeedsFallback` first (the proven BRK-style valve), with hot ones promoted later? Almost
   certainly yes, per the ADR 0001 staged approach — but the *alignment* check on **every** word/long
   memory access is so pervasive it may need emitted-IL handling, not fallback, to keep the JIT
   worthwhile.

5. **Field-decomposition dataset/decoder (§3, §5.2) — bigger importer change than the Z80's.** The
   68000 wants a field-pattern dataset (operation bit-pattern + size + EA-category, expanded by the
   generator), not a flat per-opcode table. The importer's single-byte opcode regex
   (`OpcodeDataset.cs:45`), 6502 byte-rules (`:146-153`), and flat-row model are further from the
   68000 than from the Z80. **Risk:** under-scoping the M4 extraction by assuming "it's like the Z80,
   just bigger" — the *shape* is different (field-decomposed), not just larger. **Open:** does M3.1b's
   "opaque key from a decode function" model (item 4 above) actually accommodate field-derived keys,
   or does M4 need a second decoder shape?

6. **`A7`/USP/SSP banking + supervisor mode (§1, §6).** The stack pointer is mode-banked; exceptions
   switch mode and stack. **Risk:** the introspection interface (`GetRegister("A7")`) must return the
   *current-mode* bank; the harness/monitor and TomHarte vectors must agree on which bank is visible.
   **Open:** model `A7` as one named register whose backing is mode-selected in the partial (the
   recommended altitude), and confirm the TomHarte m68000 vectors' state shape (do they expose
   USP/SSP separately?).

7. **TomHarte m68000 vector specifics (§7) — assumed-available, shape-unconfirmed.** The set exists
   (`SingleStepTests/m68000`), but its exact state schema (register naming, SR vs CCR, the bus-trace
   word/long transaction encoding, whether it checks the prefetch queue) is **unconfirmed in this
   read** — confirm during M4 setup. **Risk:** the recording bus must record *word/long* transactions
   in the trace (ties to §2 option A), not just bytes; if the bus stays byte-only the trace won't
   match the vectors' transaction granularity.

8. **The `X` flag (§1, §6, J-new-C).** A second carry the 6502/Z80 lack, consumed by
   `ADDX/SUBX/NEGX/ROXL/ROXR/ABCD/SBCD`. **Risk:** the flag model (still a closed `Flag` enum +
   `s_flagMembers` + `CpuEmitter.FlagBit`, scoped *out* of M3.1a) must carry `X` distinctly; if the
   M3 flag-model work bakes the Z80's `S Z Y H X P/V N C` layout without anticipating a *fully
   data-driven* flag set, the 68000's `X N Z V C` is another mirror-edit. **Recommendation:** do the
   data-driven flag model before M4 (see "M3 NOW" item 11).

9. **Confirmed non-risks (positive findings, to avoid over-engineering in M3/M4):**
   - **24-bit address bus FITS the current `addressBits ≤ 24` cap** (`AddressSpace.cs:34`, confirmed;
     ADR 0001 checkpoint `0001-…:667-669`). The two-level page table stays out of scope. **Do not
     pre-build 32-bit address support in M4** (ADR 0001 open-q 7, `0001-…:714-718`).
   - **The flat-`uint`-address assumption (J-new-B) survives the 68000 untested** — the 68000 is flat,
     just big-endian/word-accessed. Segmentation is the **8086's** job (M5), not the 68000's. Correct
     division of labour; do not conflate them.
   - **SMC/dirty-page (J8) and the fastmem *split* (J4) survive** — the 68000 reuses them (wider
     element width, same page model). Another confirmation they were never 6502-shaped.
   - **The interrupt *seam* (Decision 5) survives** — the 68000 adds policy in the partial (priority
     mask, mode switch, vector-table read) and one likely contract nudge (level-carrying line), not a
     seam redesign.

---

*End of brief. Provisional/structural material is labelled throughout; the verified opcode table,
exact cycle model, and TomHarte vector schema are M4 extraction deliverables, not claims of this
document.*
