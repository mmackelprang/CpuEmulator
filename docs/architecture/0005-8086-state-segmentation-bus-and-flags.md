# ADR 0005 — 8086/8088 state model, segmentation + addressing, the little-endian bus reuse, and the FLAGS model (Milestone M5, foundation half 1)

> **Status:** Accepted (architecture pass — design ahead of implementation; M5 still follows M4.5 → M4.6 in the queue)
> **Date:** 2026-06-15
> **Deciders:** Mark (owner); this ADR is the decision record the M5 Planner + Builder consume across the 8086 arc.
> **Supersedes / relates to:**
> - **ADR 0003** (`0003-68000-state-width-and-bus.md`) — generalized the substrate the 8086 now reuses: variable register width (`Bits ∈ {8,16,32}`), the wide bus with `Endianness` as a bus property, `FlagBitDef.Bit` range `0–15`, the `HighHalf`/`LowHalf` pair-view register machinery (originally Z80, exactly the shape `AX`/`AL`/`AH` needs). **Read 0003 first; this ADR leans on its decisions and cites where the 8086 reuses them as-is vs diverges.**
> - **ADR 0004** (`0004-68000-decode-addressing-and-exceptions.md`) — the decode/EA/exception companion. The 8086's decode + ModR/M + EA + instruction-set scope + the M5 PR arc + the TomHarte recon are in the **companion ADR 0006**; **0005 + 0006 together are the M5 foundation, mirroring the 0003 + 0004 split.**
> - **ADR 0002** (`0002-address-space-scaling.md`) — the flat page table caps at 24 address bits; the 8086's 20-bit physical address (1 MB) fits with room to spare. No address-space scaling work.
> - The M4 status pointer `docs/superpowers/plans/2026-06-15-m4-status-and-resume.md` (the M5 line: "the entire 8086 milestone … needs its own Architect pass: segmentation, ModRM decode, the flag model, the instruction set; its own ADRs + multi-PR arc"). This ADR pair is that pass.

---

## 1. Context

### 1.1 Why an Architect pass now, and what is DIFFERENT about the 8086

The 6502 (M1/M2) and Z80 (M3) reused existing seams; the 68000 (M4) generalized the substrate across **state width, the size axis, a wide big-endian bus, word-granular field decode, a structured EA descriptor, and vectored exceptions**. The 8086 sits between: it forces **fewer NEW state primitives than the 68000** (its registers are 16-bit, the bus is little-endian like the 6502/Z80, the flags are a single byte-plus-a-bit), but it forces **one genuinely new front-end primitive the substrate has never built: a byte-granular, variable-length, prefix-stacking decode with operand-determined length** (ADR 0006 Decision 1 — the load-bearing M5 finding), and **one new addressing concept: segmentation** (seg:offset → 20-bit physical). This ADR decides the **state, segmentation/addressing, bus, and flag** half; ADR 0006 decides the **decode/ModR/M/EA/instruction-set** half.

The organizing question (mirroring ADR 0003): **where the 68000 already widened a seam, does the 8086 ride it for free — and where the 8086's segmented little-endian shape diverges from the 68000's flat big-endian shape, what is the minimal additive change?** As with every prior architecture, the invariant is: **the 6502, Z80, and 68000 stay byte-identical** (their `RegeneratedSpec` guards + TomHarte/ZEX gates), and every change is additive.

### 1.2 The 8086/8088 in one paragraph (the state/data dimensions)

The Intel 8086 is a 16-bit CISC processor with a **16-bit programming model** and a **20-bit physical address space (1 MB)** reached via **segmentation**: every memory reference forms a physical address as `(segment << 4) + offset`. Eight 16-bit general/index registers — `AX BX CX DX` (each decomposable into 8-bit `AH/AL`, `BH/BL`, `CH/CL`, `DH/DL` halves), `SP BP SI DI` (NOT byte-decomposable) — four 16-bit **segment registers** `CS DS ES SS`, a 16-bit instruction pointer `IP`, and a 16-bit `FLAGS` register. It is **little-endian** (low byte at the lower address), has **no alignment requirement** (an unaligned word access is legal, merely costing the 8088 extra bus cycles), and is **flat-within-a-segment** (a segment is a 64 KB window; offset arithmetic wraps within 16 bits — the `(seg<<4)+offset` sum, however, can carry into the 20th bit). The **8088** is the same programming model behind an **8-bit external data bus** (every 16-bit transfer is two byte cycles) — which is exactly why the SingleStepTests vectors are an *8088* set (ADR 0006 Decision 5): the byte-granular bus trace is the 8088's, and it is the natural fit for our little-endian byte bus.

### 1.3 What the substrate already gives the 8086 (verified against the live tree)

The M4 substrate (PRs #33–#38) generalized exactly the seams the 8086 needs for its *state and bus* half. Verified file:symbol citations:

- **Register width + 8-bit half-views.** `RegisterDef` (`src/CpuEmulator.Core/Specification/RegisterDef.cs:12`) is `record RegisterDef(string Name, int Bits, RegisterRole Role, string? HighHalf, string? LowHalf)`. The parser accepts `Bits ∈ {8,16,32}` (`SpecParser.cs:509`, message "register width must be 8, 16, or 32 bits"). The **`HighHalf`/`LowHalf` pair-view machinery** (Z80 M3.4a) validates that a pair-view register is **16-bit** and its two halves are **declared 8-bit registers** (`SpecParser.cs:549,556`). **This is exactly the `AX`/`AH`/`AL` shape** — `AX` = 16-bit pair-view over the declared 8-bit `AH`/`AL`. The 8086 reuses this with **zero framework change** (the Z80 `BC`/`B`/`C` precedent is structurally identical).
- **`RegisterRole`** (`RegisterRole.cs:3`) = `{ General, ProgramCounter, Status, StackPointer }`. **No segment-register role exists** (decision below: segment registers are `General`; segmentation is a partial-level concern, not a register-role concern).
- **The bus is already little-endian-default + wide.** `IAddressSpace` (`src/CpuEmulator.Core/IAddressSpace.cs`) carries `Read8/Write8/TryPeek8`, the wide `Read16/Read32/Write16/Write32` as **default interface methods composing over `Read8`/`Write8`**, and `Endianness Endianness => Endianness.LittleEndian` (the default). `Endianness` (`Endianness.cs:11`) = `{ LittleEndian, BigEndian }`, `LittleEndian = 0`. `AddressSpace` (`AddressSpace.cs`) takes `Endianness endianness = Endianness.LittleEndian` at construction (`AddressSpace.cs:31`) — **the 8086 constructs the DEFAULT** (unlike the 68000, which constructs `BigEndian`). The page table is 256-byte pages (`AddressSpace.cs:10`), capped at 24 address bits (`AddressSpace.cs:36`). **The 8086's 20-bit address fits** (`1<<20` = 1 MB = 4096 pages of 256 B).
- **Alignment is detection-only.** `BusAlignment.IsMisaligned(uint address, AccessWidth width)` (`BusAlignment.cs:17`) returns `true` iff `width != Byte && (address & 1) != 0`. It **never raises** (the 68000's M4.5 interpreter checks it before raising address-error). **The 8086 simply never calls it** — unaligned access is legal. (An 8088 *timing* model may consult it for the extra-cycle penalty; that is the late timing axis, ADR 0006 Decision 6.)
- **The flag vocabulary is already broad.** `Flag` (`src/CpuEmulator.Core/Specification/Flag.cs:13`) = `{ C=0, Z=1, I=2, D=3, V=6, N=7, S=8, H=9, P=10, Y=11, X=12 }` — a per-spec *name* vocabulary, NOT bit positions on any one CPU (each spec's `FlagLayout` assigns real bit positions). `FlagLayout` (`FlagLayout.cs:10`) = `record FlagLayout(FlagBitDef[] Bits)`; `FlagBitDef` (`FlagLayout.cs:14`) = `record FlagBitDef(string Name, int Bit)`; the parser accepts `Bit ∈ [0,15]` (`SpecParser.cs:920`). The 8086's FLAGS needs **CF/PF/AF/ZF/SF/TF/IF/DF/OF** — six map to existing `Flag` members; three are new (decision below).
- **The JIT seam is generic** (M3.5-3c): a third — now a fourth — CPU enters as all-fallback with zero JIT-assembly change. The 8086's JIT bring-up is M5.6 (all-fallback), and its hot-op emit arm is the post-M5 cross-arch M6 phase (ADR 0006 §3) — exactly the 68000's posture.

The headline: **the 8086's state + bus + flag half is almost entirely a reuse of M4's generalizations** — the register pair-view, the little-endian-default wide bus, the 0–15 flag-bit range, and the all-fallback JIT seam all already exist. The genuinely new work in *this* ADR is **segmentation** (Decision 2); the genuinely new work overall is the **decode front-end** (ADR 0006 Decision 1).

---

## 2. Decisions

Every decision keeps the 6502/Z80/68000 byte-identical (their `RegeneratedSpec` guards + gates) and is additive.

### Decision 1 — The register file: 16-bit pair-views (`AX`/`AH`/`AL`) reuse the Z80 machinery; segment registers + `IP` + `FLAGS` are plain declarations; segment regs are `General`-role

**The problem.** The 8086 register file is: four 16-bit pair-view accumulators (`AX`=`AH`:`AL`, `BX`=`BH`:`BL`, `CX`=`CH`:`CL`, `DX`=`DH`:`DL`), four 16-bit pointer/index registers with **no byte halves** (`SP`, `BP`, `SI`, `DI`), four 16-bit **segment registers** (`CS`, `DS`, `ES`, `SS`), a 16-bit `IP`, and the 16-bit `FLAGS`.

**Decision.** Declare:
- `AH AL BH BL CH CL DH DL` — eight 8-bit `General` registers (the backing fields).
- `AX BX CX DX` — four 16-bit `General` **pair-views** (`HighHalf="AH", LowHalf="AL"`, etc.), reusing `SpecParser.cs:549,556` validation. No backing field — computed views, exactly as the Z80's `BC`/`DE`/`HL`.
- `SP BP SI DI` — four 16-bit `General` registers (no halves). `SP` MAY take `RegisterRole.StackPointer`; `BP` is `General`.
- `CS DS ES SS` — four 16-bit **`General`** registers (see rationale below).
- `IP` — 16-bit `RegisterRole.ProgramCounter`.
- `FLAGS` — 16-bit `RegisterRole.Status`.

**Why segment registers are `General`, not a new role.** The 68000 precedent (ADR 0003 §4 / M4.1 resolution) settled that **mode/identity concerns that the generator does not need to special-case stay in the hand-written partial**, not in `RegisterRole`. `RegisterRole` exists so the generator/monitor know *which register is the PC*, *which is the SP*, *which is the status word* — structural facts the generated `Step`/introspection use. A segment register is just a 16-bit value the **EA computation** (Decision 2, in the partial) reads to form a physical address; the generator never needs to know "this is a segment register." Adding a `SegmentRegister` role would be the same over-specification the 68000 avoided for `USP`/`SSP` (kept in the partial, declared as `General`/`StackPointer`). **Recommend: `CS DS ES SS` are `General`; segmentation lives in the partial's EA layer.** (Open question 1 revisits this only if the disassembler/monitor turns out to need the tag — unlikely.)

**Consequences.**
- *Good:* the entire register file is **existing machinery** — the pair-view is the proven Z80 seam; `IP`/`FLAGS`/`SP` are ordinary role declarations. **Zero framework change** for the register file. The TomHarte 8088 vectors name `ax bx cx dx sp bp si di cs ds es ss ip flags` (ADR 0006 Decision 5) — all declarable names.
- *Bad:* none of significance. The one subtlety is the **partial-register-write hazard** the 68000 already taught (ADR 0003 Decision 1): a write to `AL` must preserve `AH` and vice-versa, and a 16-bit write to `AX` must update both halves. The pair-view machinery handles the *view*; the **op bodies** must do partial writes correctly (write `AL` = update the low-8 backing, leave `AH`). This is the Z80's `B`/`C`/`BC` discipline, already proven — not new, but a known TomHarte-caught bug class (flagged for the M5 interpreter plan).

### Decision 2 — Segmentation: physical address = `(segment << 4) + offset`, computed in the partial's EA layer; the default-segment-per-mode rule + segment-override prefixes are EA-descriptor data; the existing flat 20-bit `IAddressSpace` is the physical bus unchanged

**The problem — the one genuinely new addressing concept.** The 68000 is **flat**: an EA *is* a physical address. The 8086 is **segmented**: an EA computation produces a **16-bit offset within a segment**, and the **physical** address is `(segReg << 4) + offset`, masked to 20 bits. Which segment register applies is **mode-dependent** (the "default segment" rule) and **overridable** by a one-byte prefix (`2E`=CS, `36`=SS, `3E`=DS, `26`=ES). This is a layer the EA machinery (ADR 0004 Decision 2 / the 68000's `ComputeEa`) has never had: the 68000's `ComputeEa` returns *the* address; the 8086 needs `physical = (segmentFor(mode, override) << 4) + offset(mode, modrm, disp)`.

**The default-segment rule (the data the EA layer needs):**
- Code fetch (`IP`): segment = **`CS`** (never overridable).
- Stack operations (`PUSH`/`POP`/`CALL`/`RET`, and any EA using `BP` as a base): segment = **`SS`**.
- General data (most `[mem]` operands; any EA using `BX`/`SI`/`DI` without `BP`): segment = **`DS`**.
- String destination (`ES:DI` for `STOS`/`MOVS`/`CMPS`/`SCAS` destination): segment = **`ES`** (the `DI` destination is **not** overridable; the source `SI` operand defaults to `DS` and **is** overridable).
- A segment-override prefix replaces the default for the *next* memory operand (with the documented exceptions: code fetch, the string `ES:DI` destination, and the stack-pointer push/pop are not overridable).

**Options for where physical-address formation lives.**

- **(A) Form the physical address in the 8086's hand-written partial EA layer — a new `EmitX86Ea`/`ComputeEa`-analogue that takes the ModR/M-derived offset + the resolved segment register and returns `(seg << 4) + offset` masked to 20 bits; the existing flat 20-bit `IAddressSpace` is the unchanged physical bus.**
  - *Pros:* segmentation is a **CPU-specific computation**, exactly the altitude the 68000's `ComputeEa` (`CpuEmitter.cs:4274`, emitted `private uint ComputeEa(...)`) occupies — and the 68000 proved the EA layer is "M68k-named but structurally generic" (it takes mode/register as operands, produces an address). The 8086's analogue is a **new `EmitX86Ea` generator emitter + the segment-resolution helper in the partial**, paralleling `EmitM68kEa` (`CpuEmitter.cs:4270`). The physical bus is **untouched**: `Read8`/`Write8`/`Read16`/`Write16` over a 20-bit-configured `AddressSpace` (LE default) — the wide LE methods (M4.2) compose two byte transactions, which is *exactly* the 8088's byte-bus behavior the vectors record. The segment shift + 20-bit wrap is pure arithmetic on a `uint`. The default-segment rule + the override are **EA-descriptor data** (which segment, whether an override is in force), resolved per memory operand.
  - *Cons:* a net-new `EmitX86Ea` emitter + the 16-bit modular offset arithmetic (offset adds wrap at 16 bits *before* the segment shift — a real 8086 quirk: `[BX+SI]` where the sum exceeds `0xFFFF` wraps within the segment, it does NOT carry into the segment base). This wrap, and the default-segment table, are the two TomHarte-caught correctness risks. But it is the **right home** — the 68000 established the EA layer as the place addressing lives.
- **(B) Push segmentation into `IAddressSpace` (a "segmented address space" that knows segment registers).**
  - *Pros:* superficially "the bus owns addressing."
  - *Cons:* **rejected.** Segment registers are **CPU architectural state** the bus has no business reading; the default-segment-per-mode rule is **decode/operand-shaped** (it depends on the addressing mode and the override prefix), which the bus never sees. This would smear CPU semantics into the shared bus contract — the exact anti-pattern ADR 0003 Decision 2(B) rejected (don't let a CPU-ism leak into `IAddressSpace`). The 68000 keeps `ComputeEa` in the CPU; the 8086 keeps `(seg<<4)+offset` in the CPU. The bus stays a **flat 20-bit physical** space.
- **(C) Model the address space as the full 20-bit flat array and have the CPU only ever pass physical addresses (same as B's bus, but segmentation entirely in op bodies, no EA-layer helper).**
  - *Pros:* minimal abstraction.
  - *Cons:* **rejected** as *under*-structured — it scatters `(seg<<4)+offset` across every memory-touching op body (hundreds of sites), repeating the default-segment logic and the 16-bit-offset-wrap everywhere. The 68000 deliberately centralized EA computation in `ComputeEa` to avoid exactly this; the 8086 should too. (B) and (C) differ only in where the duplication lands; both lose the single-home property.

**Decision: (A) — segmentation is formed in a new `EmitX86Ea`/segment-resolution EA layer in the 8086 partial; the physical bus is the unchanged flat 20-bit little-endian `AddressSpace`.** Concretely:
- The 8086 EA layer computes the **16-bit offset** from the ModR/M descriptor (ADR 0006 Decision 2/3) and the **segment register** from `(default-segment-for-mode, override-prefix)`, then returns `physical = ((uint)segReg << 4) + offset) & 0xFFFFF`. The offset arithmetic (base + index + disp) **wraps at 16 bits before the shift** (the segment-relative wrap quirk).
- The **default-segment rule is data** carried on the resolved instruction/EA descriptor; the **segment-override prefix** (one of `26/2E/36/3E`) is consumed by the decode front-end (ADR 0006 Decision 1) and threaded to the EA layer as "use this segment instead of the default."
- The `AddressSpace` is constructed with `addressBits: 20` and the **default `Endianness.LittleEndian`** — no new bus surface, no big-endian path, no alignment enforcement.

**Consequences.**
- *Good:* segmentation lives in **one place** (the EA layer), mirroring the 68000's centralized `ComputeEa`; the physical bus is **100% reused** (flat 20-bit LE — the cheapest possible bus result, the inverse of M4 where the bus was the load-bearing change); the default-segment rule + override are **data**, not scattered branches. The 8088's two-byte-cycle 16-bit access falls out of the LE wide-method composition for free.
- *Bad:* the EA layer gains the segment shift + the 16-bit-offset-wrap + the default-segment table — the new correctness surface (TomHarte-caught). The `EmitX86Ea` emitter is net-new generator code (paralleling `EmitM68kEa`). The 8086 does NOT reuse the 68000's `ComputeEa` (it is `M68k`-shaped: 14 Motorola modes, the `(An)+`/`-(An)` write-back, `ExtensionWords`); it needs its own — but it **reuses the pattern + the descriptor approach + the displacement-helper seam** (`EmitZ80IndexedEa`, `CpuEmitter.cs:2209`, the base+signed-displacement helper the 68000 also reused). See ADR 0006 Decision 3 for the ModR/M EA-descriptor detail.

### Decision 3 — The FLAGS model: six flags reuse existing `Flag` members; add `A` (auxiliary-carry), `T` (trap), `D̄` (direction) as new vocabulary; FLAGS is a 16-bit `Status` register via `FlagLayout`

**The problem.** The 8086 `FLAGS` is 16-bit with nine defined bits: `CF`(0) `PF`(2) `AF`(4) `ZF`(6) `SF`(7) `TF`(8) `IF`(9) `DF`(10) `OF`(11). Of these, six have a clear existing `Flag` member; three do not.

**Mapping against the existing `Flag` enum (`Flag.cs:13`):**

| 8086 bit | 8086 flag | Existing `Flag` member | Status |
|---|---|---|---|
| 0 | CF carry | `C` | reuse |
| 2 | PF parity | `P` | reuse (Z80 parity) |
| 4 | AF aux-carry (BCD half-carry) | — | **NEW** (semantically the Z80 `H` half-carry; see note) |
| 6 | ZF zero | `Z` | reuse |
| 7 | SF sign | `S` | reuse (Z80 sign) |
| 8 | TF trap (single-step) | — | **NEW** |
| 9 | IF interrupt-enable | `I` | reuse (note polarity, below) |
| 10 | DF direction (string ops) | — | **NEW** |
| 11 | OF overflow | `V` | reuse |

**Decision.** 
- **Reuse** `C`, `P`, `Z`, `S`, `V`, `I` for `CF`, `PF`, `ZF`, `SF`, `OF`, `IF` (assigning the 8086 bit positions via `FlagLayout`, which is per-spec data — `Flag` members are *names*, not fixed positions).
- **Add three `Flag` members** for the 8086-specific flags: an **auxiliary-carry** flag, a **trap** flag, and a **direction** flag. *Recommendation on naming:* reuse the existing `H` member for the auxiliary carry (the AF *is* the BCD half-carry — bit 4 carry-out of `AAA`/`AAS`/`DAA`/`DAS` — and `H` already means exactly "half-carry"; this avoids a synonym), and add **two** new members `T` (trap) and a direction flag (suggest a distinct letter, e.g. an explicit `Df`/`Dir` member — note the existing `D` member is the 6502 *decimal* flag at a different bit, so the 8086 direction flag must be a **new, distinctly-named** member, NOT a reuse of `D`). **Net: reuse `H` for AF; add `T` (trap) and a direction member.** (Just-in-time: confirm at the M5 flag PR whether `H` is acceptable for AF or a dedicated `Af` reads cleaner; both are one-line additive enum edits. See open question 2.)
- **FLAGS** is a single 16-bit `RegisterRole.Status` register; its bit layout is declared in the spec's `FlagLayout` with `FlagBitDef("C",0)`, `("P",2)`, `("A"/"H",4)`, `("Z",6)`, `("S",7)`, `("T",8)`, `("I",9)`, `("Dir",10)`, `("V",11)`. The `Bit ∈ [0,15]` range (`SpecParser.cs:920`) already covers all of these (the 68000's 16-bit SR forced that relaxation — the 8086 rides it).

**Two semantic notes the op bodies must encode (TomHarte-caught):**
- **Parity is even-parity of the low 8 bits of the result** (PF=1 when the low byte has an even number of set bits) — the same parity the Z80 `P` computes; reuse the helper if one exists, else a small lookup. This is computed even for 16-bit results (only the low byte counts).
- **The direction flag controls string-op address stepping** (`DF=0` ⇒ `SI`/`DI` increment; `DF=1` ⇒ decrement), set/cleared by `CLD`/`STD`. This is the 8086's analogue of "a flag that steers an instruction's behavior" (cf. the 68000 had none of this; the closest prior art is none — it is a new flag *role*, but mechanically it is just a bit read by the string ops in the partial). The `IF` (interrupt-enable) flag is also read by the interrupt seam (Decision 4) — note the **polarity is opposite the 6502/65xx `I`**: 8086 `IF=1` *enables* interrupts, where the 6502 `I=1` *disables* them. The `Flag` *name* `I` is reused, but the partial's interrupt policy must use the 8086 sense (this is policy in the partial, not a `Flag`-enum concern).

**Consequences.**
- *Good:* the flag *infrastructure* is entirely reused (per-spec `FlagLayout`, the 0–15 bit range from the 68000, the name-not-position `Flag` vocabulary); the only framework touch is **adding two or three `Flag` enum members** — the same additive move M3.4a/M4.1 made (the enum is designed to grow). Six of nine 8086 flags map to existing names.
- *Bad:* three semantic subtleties (parity = low-byte even-parity; direction steers string ops; `IF` polarity is inverted vs the 6502 `I`) are op-body/partial-policy concerns the M5 interpreter plan must call out explicitly — each is a TomHarte-caught bug class if mis-modeled. None is a framework change.

### Decision 4 — Interrupts/exceptions: the generic `TryServiceInterrupt`/`InterruptPending` seam carries the 8086; the vector table is `[type*4]` in low memory (offset:segment pairs); `INT n`/`INT3`/`INTO`/divide-error are synchronous fallback-class ops; NO IPL-level line needed

**The problem.** The 8086 has a **256-entry interrupt vector table at physical address 0**, each entry a **4-byte far pointer (16-bit offset then 16-bit segment, little-endian)** — `IP = mem[type*4]`, `CS = mem[type*4 + 2]`. Interrupt/exception entry **pushes FLAGS, then CS, then IP** onto the `SS:SP` stack, **clears IF and TF**, and vectors. Sources: external maskable `INTR` (gated by `IF`, vector supplied by the device on the ack cycle), non-maskable `NMI` (vector 2, always), and **synchronous CPU exceptions**: divide-error (vector 0), single-step/trap (vector 1, when `TF` set), breakpoint `INT3` (vector 3), `INTO` overflow-trap (vector 4 when `OF` set), and the software `INT n` (any vector 0–255). `IRET` pops `IP`, `CS`, `FLAGS`.

**Decision: reuse the generic interrupt seam unchanged; three scoped notes.**
1. **No IPL-level line.** The 8086's external interrupt is a **single maskable line gated by one bit (`IF`)** plus `NMI` — exactly the 6502/Z80 `IRQ`/`NMI` boolean shape (`IInterruptLine`, `IInterruptLine.cs`). The **68000's IPL-level line (ADR 0004 Decision 3, the one M4 contract growth) is NOT needed** — the 8086 is a step *back* toward the boolean line. The 6502/Z80 boolean `IInterruptLine` carries the 8086 directly; the vector number on a maskable ack is supplied by the device (a partial-policy detail, like the 68000's vectored/autovector choice). **Zero interrupt-contract change.**
2. **The vector table read uses the little-endian wide bus.** `IP = Read16(type*4)`, `CS = Read16(type*4 + 2)` over the flat 20-bit LE `AddressSpace` — the wide LE methods (M4.2) compose this for free. The push sequence (`FLAGS`, `CS`, `IP`) and the `IF`/`TF` clear are **partial policy**, the same altitude as the 6502's `$FFFE` sequence and the 68000's SSP-frame push.
3. **Synchronous CPU-raised vectors are fallback-class ops.** `INT n`/`INT3`/`INTO`/divide-error/single-step raise mid-instruction and vector. This is the proven **`NeedsFallback` + `EndsBlock` valve** (the 6502 `BRK` precedent; `JitOpClass.Flow`, `OpcodeDescriptor.cs:12`), and since the 8086 is **all-fallback in M5 anyway** (ADR 0006 §3, the M3.5/M4.6 discipline), this is automatic. The single-step trap (`TF`-driven, vector 1) checked **after** each instruction is a partial-policy detail (a post-instruction `TF` check, like the 68000's trace bit) — flagged for the late M5 exception/timing sub-milestone (ADR 0006 Decision 6), not the early correctness PRs.

**Consequences.**
- *Good:* the interrupt seam survives a **fourth** architecture with **no contract change at all** (the 8086 is *simpler* than the 68000 here — a boolean line, not an IPL level), strengthening the positive-proof point. The vector read + push + `IF`/`TF` clear are partial policy at the established altitude. The synchronous vectors are the proven fallback valve.
- *Bad:* the `INT n` software-interrupt family + the `TF` single-step machinery + the `IF`-polarity-correct masking are partial code (more logic, not a seam change). The single-step-after-every-instruction behavior interacts with the timing axis (it is a per-instruction post-step) and should land in the late exception/timing sub-milestone, not be force-fit into the first MOV/ALU PR (ADR 0006 Decision 6 — the lesson-learned).

---

## 3. What the 8086 proves about genericity (the segmented/variable-length half)

ADR 0003 §3 framed the 68000 as proving the **data/memory half** (width > 16, big-endian wide bus, alignment). The 8086 proves two further dimensions the ladder had not yet tested:

- **Segmented addressing on top of the flat physical bus** — Decision 2 proves the EA layer (the 68000's `ComputeEa` altitude) is the right home for a *non-flat* address-formation rule, and that the `IAddressSpace` flat-physical contract is genuinely CPU-agnostic (it never learns what a segment is). The bus stays flat; the CPU owns the mapping. This is the inverse of M4 (where the bus was the load-bearing change and the addressing was flat).
- **The little-endian default + the 8-bit-bus (8088) trace** — the 8086 exercises the LE wide-method composition path (M4.2 built it but the 68000 only used the BE path); the 8088 vectors validate that two-byte-cycle 16-bit accesses over the LE bus produce the right trace. This closes the *other* endianness of the wide bus.

The genuinely new primitive — **byte-granular variable-length prefix-stacking decode** — is ADR 0006 Decision 1, the load-bearing M5 finding. Together with the 6502/Z80 (front half), the 68000 (data/memory half), and the 8086 (segmentation + variable-length-decode half), the four architectures leave essentially no genericity dimension untested before the M6 optimization phase.

---

## 4. Decisions deliberately left "just-in-time" (decide at the first M5 PR)

1. **Segment-register role tagging** — whether `CS/DS/ES/SS` stay `General` (recommended) or get a `SegmentRegister` role if the disassembler/monitor needs to render `CS:` prefixes or the introspection needs to group them. Decide at the first register-file PR; the recommendation (keep in the partial, `General`) follows the 68000's `USP`/`SSP` precedent.
2. **The auxiliary-carry flag naming** — reuse `H` (half-carry, semantically identical) vs add a dedicated `Af` member. One-line additive either way; settle at the flag PR with the real `DAA`/`AAA` op bodies in hand (the BCD ops are the only consumers; whichever name reads cleaner in those bodies wins).
3. **Whether `SP` takes `RegisterRole.StackPointer` or stays `General`** — the 8086 stack is `SS:SP`; the role only matters if the generator/monitor special-cases the SP. The 68000 made `SSP` the `StackPointer` role and `USP` `General`; the 8086 has one SP, so `StackPointer` is the natural tag — but confirm the generated `Step`/monitor actually consumes the role for the 8086 (if not, `General` is fine). Decide at the register-file PR.

---

## 5. Consequences summary

- **Register file:** entirely reused machinery — pair-views (`AX`/`AH`/`AL`) via the Z80 seam, `IP`/`FLAGS`/`SP` as role declarations, segment registers as `General`. **Zero framework change.**
- **Segmentation:** a new EA-layer computation (`EmitX86Ea` + segment resolution) in the 8086 partial — `(seg<<4)+offset & 0xFFFFF`, the default-segment-per-mode rule + override as data, the 16-bit-offset-wrap quirk. The physical bus is the **unchanged flat 20-bit little-endian `AddressSpace`** (default `Endianness`; 20 fits the 24-bit cap; no alignment enforcement).
- **Flags:** reuse `C/P/Z/S/V/I`; add `T` (trap), a direction member, and use `H` for AF (or add `Af`). FLAGS is a 16-bit `Status` register via `FlagLayout` (0–15 range already exists). Three op-body/policy subtleties (parity, direction-steers-strings, `IF`-polarity).
- **Interrupts:** the generic boolean `IInterruptLine` seam, **no IPL-level line** (simpler than the 68000); vector table `[type*4]` via the LE wide bus; synchronous `INT`/divide/trap as fallback-class ops; push/clear-IF-TF as partial policy.
- **No address-space scaling** (ADR 0002 holds — 20-bit fits the flat table).
- **The JIT** accepts the 8086 as all-fallback (M5.6); the hot-op emit arm is M6 (built once across Z80+68000+8086).

---

## 6. Open questions for the owner

1. **Segment-register role (Decision 1 just-in-time item 1)** — confirm `CS/DS/ES/SS` as `General` (recommended, following the 68000 `USP`/`SSP` precedent) vs a new `SegmentRegister` role. *Recommend: `General`; revisit only if the disassembler needs the tag.*
2. **Auxiliary-carry flag name (Decision 3 just-in-time item 2)** — reuse `H` for AF vs add `Af`. *Recommend: decide at the flag PR with the BCD op bodies in hand; lean `H` (semantically identical).*
3. **The 8088 (byte-bus) vs 8086 (word-bus) target framing** — the SingleStepTests set is **8088** (ADR 0006 Decision 5), whose byte bus matches our LE byte-composing `AddressSpace` exactly. Confirm M5 targets the **8088** programming-model-plus-byte-bus as the gated artifact (the programming model is identical to the 8086; only the external bus width — hence the cycle/transaction trace — differs). *Recommend: yes, gate on 8088; the trace is the byte-bus trace, which is what our bus naturally produces.* The *cycle-count* difference (the 8088's two-byte-cycle word access, the 6-byte vs 4-byte prefetch queue) is the **timing axis**, sequenced late per ADR 0006 Decision 6.

---

*End of ADR 0005. The decode front-end (the load-bearing M5 finding), ModR/M, the EA descriptor, the instruction-set scope, the M5 PR arc, the TomHarte 8088 recon, and the data-vs-timing-axis lesson-learned are in the companion ADR 0006.*
