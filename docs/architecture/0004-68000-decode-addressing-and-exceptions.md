# ADR 0004 — 68000 decode, addressing modes, exceptions/privilege, and the M4 PR breakdown (Milestone M4, foundation half 2)

> **Status:** Accepted (architecture pass — not yet implemented)
> **Date:** 2026-06-15
> **Deciders:** Mark (owner); this ADR is the decision record the M4 Planner + Builder consume across the 68000 arc.
> **Supersedes / relates to:**
> - **ADR 0003** (`0003-68000-state-width-and-bus.md`) — the companion M4 foundation half (register width, the size axis, the wide big-endian bus, the confirmed TomHarte m68000 vector schema). **Read 0003 first; 0004 assumes its decisions** (the `OperandSize` axis, `Read16/32`, the `USP`/`SSP`/`A7` model).
> - **ADR 0001** (`0001-z80-second-architecture.md`) — Decision 1 (the prefix/page decode model that "generalizes forward… the 68000's word-granular decode"), Decision 4 (the class/mode matrix "rebuilt, not extended"), Decision 5 (the interrupt seam, "already generic"), Decision 6 (extraction-as-acceptance-test), and the J9 block-model row.
> - **ADR 0002** (`0002-address-space-scaling.md`) — the 24-bit address bus fits the flat page table; no scaling work.
> - `docs/research/68000-architecture-analysis.md` (§3 decode, §4 addressing, §5 ISA scale, §6 exceptions, the opcode-space structural map) — the domain input this ratifies, corrected against the post-M3 tree and the confirmed vector schema.

---

## 1. Context

ADR 0003 decided the 68000's **state and bus**. This ADR decides how the 68000 is **decoded, addressed, and how its exceptions/privilege fit** — and lays out the **M4 PR breakdown** the Planner expands. The post-M3 tree is the baseline (verified against `main` @ `797c69c`):

- **Decode is already keyed + computed-length** (M3.1b / M3.4e). `DecodeStructure` (`src/CpuEmulator.Core/Specification/DecodeStructure.cs`) carries `PrefixByte[]` (with `CompoundWith`/`DisplacementBeforeOpcode` for the Z80 `DD CB d op` form), `ModRmOpcodes`, `SubFieldOpcodes`. `InstructionDef` (`InstructionDef.cs`) carries `Prefix`/`Prefix2`/`SubField`/`KeyShape` (`enum DecodeKeyShape { OpcodeByte, PrefixedOpcode, OpcodeGroup, Compound }`). The JIT `Discover` runs the per-CPU `IJitTarget.Decode` returning a `(key, computed-length)` and advances PC by the **computed** length (M3.5-3c §J3) — the descriptor key is already an **opaque key from a generated decode function**, not "the opcode bytes." The 6502 absent-`DecodeStructure` case is the degenerate single-byte walk (byte-identical).
- **`AddrMode`** (`AddrMode.cs`) is still a **closed enum**, now 6502 + Z80 members (`Register`, `RegisterIndirect`, `Indexed`, `ImmediateExtended`, `ExtendedAddress`, `RelativeJump`, `Bit`, `IoPort*`). It is mirrored in the JIT `JitMode`, the parser `s_addrModes`, and the importer `ValidModes`/`SupportedModes` (the 3–4-edit "mirror-table smell" ADR 0001 names). The Z80 `Indexed` mode + its EA-helper precedent (`(IX+d)` displacement EA) is the closest existing analog to the 68000's indexed modes.
- **The interrupt seam is generic** (ADR 0001 Decision 5, confirmed M3.5-3c §J6): the generated `Step` calls `partial bool TryServiceInterrupt()` and exposes `partial bool InterruptPending`; the per-CPU partial owns the policy. The JIT boundary-samples `InterruptPending` without knowing the policy. A uniform `Halted` member exists (HALT/STOP).
- **The importer** keys opcodes on a **single-byte hex regex** (`OpcodeDataset.cs`), with 6502 byte-count rules and the 6502/Z80 mode/factory vocabulary. The dataset→importer→generator→regen pipeline is the law (no hand-edited spec).

This ADR's organizing question: **does the keyed decoder, the EA layer, and the interrupt seam — all reshaped by the Z80 — extend to the 68000's word-granular field-decode, 14 EA modes, and vectored exceptions, or do they need new shape? And where the answer is "extend," how?**

---

## 2. Decisions

### Decision 1 — Decode: a word-granular, field-decomposed `DecodeStructure` variant; the descriptor key is `(operation, size)` with mode/register as operands; length is operand-computed

**This is the highest-risk M4 decision** (research risk 5). It is the first real test of the decode model beyond byte-prefix chains.

**The problem.** The 68000 decodes a **stream of 16-bit big-endian words**. The first word encodes operation + size + addressing-mode + register as **bit fields** (the "line" decomposition — high nibble selects the operation group); zero or more **extension words** follow, **their count determined by the operand fields** (a `.l` immediate = +2 words; `d16(An)` = +1; a brief-extension index = +1; `abs.w` = +1; `abs.l` = +2). This is neither prefix-based (Z80) nor variable-byte-from-the-front (8086). It is **word-granular with operand-determined extension fetch**, and the meaningful descriptor key is the **operation + size bits**, with mode/register being *operands* — the key is **derived from fields, not equal to the bytes**. The vast majority of the 64K word space is illegal (→ illegal-instruction exception); a flat 64K table is wrong (~98% empty).

**Options.**

- **(A) A new word-granular, field-decomposed `DecodeStructure` variant: the spec declares the field grammar per operation (operation bit-pattern + size-field position + EA-field position + legal EA-category); the generated decode walk fetches 16-bit big-endian words, extracts the fields, produces an opaque `(operation, size)` key, and computes the extension-word count (hence the instruction length) from the resolved mode + size.**
  - *Pros:* this is the **forward generalization ADR 0001 Decision 1 explicitly promised** ("the spec declares its decode structure; the generator emits the walk"). The post-M3 machinery already provides the two hardest pieces: the **opaque key from a decode function** (M3.5-3c §J3 — the key is whatever `Decode` returns, satisfied by the Z80's `OperationKey`) and the **computed length** (`Discover` advances by the walk's length, not a constant). The 68000 adds two axes on top: the **fetch unit is a 16-bit big-endian word** (ADR 0001 "M3 NOW item 3" pre-planned the fetch unit as a parameter), and the **length is operand-computed from mode+size** (ADR 0001 item 5 pre-planned `Length` as "what the decode walk computed"). Matches the silicon's own field decode and WinUAE's `table68k` model.
  - *Cons:* it is a genuinely **new `DecodeStructure` variant**, larger than the Z80's prefix variant — the spec must express a *field grammar* (bit-mask/match per operation), not a prefix list. The decode walk must do bit-field extraction, not table-switch-on-prefix-byte. This is real generator work.
- **(B) Generalize the existing prefix/sub-field keying to cover the 68000 by treating the high nibble as a "prefix" and the rest as sub-fields.**
  - *Pros:* reuses `PrefixByte`/`SubField` more literally.
  - *Cons:* **rejected.** The 68000's key is not "a prefix byte then an opcode byte"; it is a *non-contiguous bit-field extraction* (operation bits + size bits scattered across the 16-bit word, with the EA 6 bits as operands). Forcing it through the prefix/sub-field model misrepresents the encoding and cannot express the operand-computed length cleanly. The `SubField` carrier was built for the 8086 ModR/M shape, not field-decomposition.
- **(C) Enumerate the full legal instruction-word space as a flat table.**
  - *Pros:* simplest decoder.
  - *Cons:* **rejected** — ~98% of 64K is illegal; this is the wasteful flat-table option ADR 0001 Decision 1(C) already rejected for the Z80, one dimension worse.

**Decision: (A) — a word-granular, field-decomposed `DecodeStructure` variant.** Concretely:
- `DecodeStructure` (or a sibling record) gains a **fetch-unit declaration** (byte for 6502/Z80/8086, **word-big-endian** for the 68000) and a **field-grammar** representation: per operation, a `(mask, match)` over the 16-bit word plus the bit-positions of the size field and the EA field(s). The 6502/Z80 absent/prefix forms are unchanged (the new variant is opt-in; their `KeyShape`s stay `OpcodeByte`/`PrefixedOpcode`/`Compound`).
- The generated decode walk: fetch word → match against the field grammar → extract `(operation, size, ea-mode, ea-register)` → produce the opaque descriptor key `(operation, size)` → compute extension-word count from `ea-mode × size` → return `(key, totalLength)`. `Discover` advances PC by `totalLength` (already the contract). Illegal patterns return the illegal-instruction marker (which vectors — Decision 3).
- The mode/register bits are carried as **operands** on the resolved instruction (the EA descriptor — Decision 2), not as part of the key. This is why the key must be opaque-from-the-function, exactly as M3.5-3c §J3 established.

**Consequences.**
- *Good:* the keyed-descriptor + computed-length + opaque-key machinery the Z80 forced is **reused** — the 68000 adds the word fetch unit and field extraction, not a new pipeline. Block discovery (what the M6 optimizer reasons about) stays decode-driven. The DDCB-style "length is not a per-opcode constant" lesson (M3.5-3c §J3) extends cleanly to operand-computed length.
- *Bad:* the field-grammar `DecodeStructure` variant is new generator code and the **single highest-risk M4 abstraction** (research risk 5). The risk is under-scoping it as "the Z80 prefix model, bigger" — the *shape* is different (field-decomposed, word-granular), not just larger. Mitigation: prove the variant against a synthetic field-grammar fixture (the M3.1b discipline: "ship the SHAPE + the synthetic proof" before any real CPU declares one) before the 68000 dataset depends on it.

### Decision 2 — Addressing: model the 14 EA modes with a 6-bit `(mode:register)` EA descriptor + extension-word formats; the auto-inc/dec modes carry an operand-size-magnitude register write-back; the legality matrix becomes EA-category data

**The problem.** The 68000 has 14 effective-address modes (the richest set yet): `Dn`, `An`, `(An)`, `(An)+`, `-(An)`, `d16(An)`, `d8(An,Xn)`, `abs.w`, `abs.l`, `d16(PC)`, `d8(PC,Xn)`, `#imm`, plus the implied/special forms. The EA is encoded as **6 bits = mode(3) : register(3)**, near-uniform across the ISA (mode `111` is an escape whose register sub-field selects abs.w / abs.l / d16(PC) / d8(PC,Xn) / immediate). Two things stress the EA layer beyond anything the Z80's `Indexed` mode forced:

1. **`(An)+`/`-(An)` mutate the address register as a side effect of the access, by the operand size** (`.b`→±1, `.w`→±2, `.l`→±4; with the special case that `(A7)+`/`-(A7)` always move by 2 to keep the stack word-aligned). The framework's EA computation today is pure-functional — it computes an address and never mutates architectural state. These are the **first EA modes with a side effect on a register**.
2. **The class/mode legality matrix is far richer and largely orthogonal.** The current matrix (`ValidateModeForClass`) encodes 6502-specific rules, and `RequiredIndexRegister` hardcodes "the index register is named `X`/`Y`" — **meaningless for the 68000**, where the index register is *any* `An`/`Dn` named in the brief extension word. On the 68000, legality is a property of the instruction's **EA-category** (data-alterable, memory-alterable, control, etc.) applied near-orthogonally.

**Options for the EA representation.**

- **(A) An `AddrMode` enum extension (the 6502/Z80 precedent) — add ~12 members, mirror them in `JitMode`/`s_addrModes`/`ValidModes`/`SupportedModes`.**
  - *Cons:* pays the 3–4-edit mirror-table tax a fourth time (ADR 0001 "M3 NOW item 11" / research item 11), and the EA modes that need an *extension-word format* + a *register write-back side effect* don't fit a flat enum member cleanly.
- **(B) Model the EA as a structured descriptor: a `(mode, register)` pair + an extension-word-format tag + a `writeBack` (none / post-increment / pre-decrement) flag whose magnitude is the operand size. Add only the small number of new `AddrMode` members the disassembler/assembler genuinely need, and represent the rest as EA-descriptor data.**
  - *Pros:* matches the 6-bit `(mode:register)` silicon encoding; the EA-compute layer becomes a function of the descriptor (reusing the Z80 `Indexed` EA-helper precedent for the displacement/index forms); the auto-inc/dec write-back is **one capability** parameterized by size, not 6 enum members; the legality matrix keys on EA-category data, not a per-class `switch`.
  - *Cons:* a richer EA model than a flat enum; the disassembler/assembler must render `(An)+`/`-(An)`/`d8(An,Xn)` from the descriptor.

**Decision: (B) — a structured EA descriptor.** Concretely:
- An **EA descriptor** carries `(mode3, register3)`, the extension-word format (none / displacement16 / brief-index / abs16 / abs32 / immediate / pc-displacement / pc-index), and a `writeBack ∈ { None, PostInc, PreDec }`. The EA-compute layer computes the address (reusing the Z80 indexed-EA helper shape for displacement/index) and, for `PostInc`/`PreDec`, **mutates the named `An` by the operand size** (with the `A7` ±2 special case). This is the **new EA capability: register write-back with operand-size magnitude** — the first EA with a side effect on architectural state.
- The **legality matrix becomes EA-category data** (data-alterable / memory-alterable / control / alterable, the classic 68000 categories), computed from the field grammar (Decision 1), replacing the 6502 per-class `switch`. **`RequiredIndexRegister`'s fixed `X`/`Y` convention is retired** — the index register is an operand read from the brief extension word, not a fixed register name. ADR 0001 Decision 4 already predicted "the class/mode matrix substantially rebuilt, not extended"; the 68000 confirms and amplifies it.
- **PC-relative modes** (`d16(PC)`, `d8(PC,Xn)`): the EA depends on PC at the instruction's address — compile-time-resolvable in the JIT (a small M6 optimizer win, like the 6502 branch targets). `LEA`/`PEA` compute an EA *into a register / onto the stack with no memory access* — a **pure-EA op**, novel; the EA layer must support "compute the address, do not dereference."

**Consequences.**
- *Good:* the EA model matches the silicon's uniform 6-bit encoding; the auto-inc/dec side effect is data (size-parameterized), not 12 enum members; the legality matrix is EA-category data, killing the mirror-table tax for modes and the dead `X`/`Y` index convention. The Z80 `Indexed` EA-helper is the reuse seam for the displacement/index forms.
- *Bad:* the EA-compute layer gains its first architectural-state side effect (the register write-back) — the interpreter and (M6) JIT must sequence it correctly relative to the access (post-increment reads `An` *then* adds; pre-decrement subtracts *then* reads). Under-modelling the `A7` ±2 special case or the post/pre ordering is a TomHarte-caught bug class.

### Decision 3 — Exceptions/privilege: the interrupt seam extends; the 68000 adds an IPL *level* line, supervisor-mode banking, a 256-entry vector table read from low memory, and a NEW synchronous mid-instruction exception class

**The problem.** The 68000 has a 256-entry exception vector table at address 0 (each a 32-bit pointer): reset (0/1 = initial SSP + PC), bus error (2), address error (3), illegal (4), divide-by-zero (5), CHK (6), TRAPV (7), privilege violation (8), trace (9), line-A/line-F (10/11), the autovector interrupts (25–31), the TRAP #0–15 software traps (32–47), user/device vectors (64–255). It has **7 prioritized interrupt levels** gated by the 3-bit `SR` mask (level 7 non-maskable); on acknowledge a device supplies a vector or signals autovector. Exceptions/interrupts **switch to supervisor mode**, push a frame (PC + SR; a larger frame for bus/address error) onto the **SSP**, and vector through the table; `RTE` restores SR (hence mode) + PC. And — the genuinely new dimension — **synchronous CPU-raised exceptions** (address error on an odd-address word/long access, illegal instruction, divide-by-zero, TRAP, privilege violation, CHK, TRAPV) are raised **mid-instruction** and vector just like an interrupt.

**Decision: keep the generic `TryServiceInterrupt`/`InterruptPending` partial seam (ADR 0001 Decision 5, confirmed generic); the 68000 partial implements its policy. Three additions, scoped:**

1. **An interrupt-line *level*, not a bool — the one likely contract nudge.** `IInterruptLine` carries a bool today (`IInterruptLine.cs`). The 68000 needs a **3-bit IPL level** (0–7) compared against the `SR` mask. *Option:* either a level-carrying interrupt line, or the machine encodes level into which of seven lines is asserted and the partial reads the highest. **Recommend: add a level-carrying input** (additive — the 6502/Z80 `IRQ`/`NMI` stay boolean; a level is a generalization a boolean line is the degenerate case of). This is the single enumerated interrupt-seam contract growth M4 forces (research §6.2, item 10). The priority/mask *comparison* and the mode-switch/SSP-push/vector-read *sequence* are **policy in the partial** — more logic, not a seam change (the 6502's fixed `$FFFE` sequence lives at exactly this altitude).
2. **Supervisor mode + SSP banking + the data-driven vector table read.** Mode switching and the SSP bank are the ADR 0003 `USP`/`SSP`/`SR.S` model; the vector table is read from low memory via the wide bus (`Read32` of `mem[vector*4]`, big-endian — ADR 0003 Decision 2). All policy in the partial.
3. **A NEW JIT block-exit flavour: the synchronous mid-instruction vector.** The JIT's exit set (Normal / Budget / Recompile + the interrupt boundary sample) has no "conditional, mid-instruction vector." An emitted op that *might* fault (a `DIVU` that might divide by zero, **any word/long access that might be misaligned**) must be able to bail to the exception path. **Decision: exception-capable ops are `NeedsFallback` first** (the proven BRK-style valve — and the 68000 is all-fallback in M4 anyway per ADR 0003 Decision 3, so this is automatic for M4). The promotion question — whether the *pervasive* alignment check on every word/long access needs emitted-IL handling rather than fallback to keep the JIT worthwhile — is an **M6** design item (research risk 4), not M4.

**`STOP` reuses the Z80 `HALT` no-busy-spin.** The uniform `Halted` member (M3.5-3c §J6) is a direct M3→M4 reuse — `STOP` maps to the generic halted state with no new mechanism (ADR 0001 "M3 NOW item 9" pre-planned exactly this).

**The address-error / alignment check has its home in the bus (ADR 0003 Decision 2).** Because the wide bus knows both the address and the access width, the even-alignment check on word/long access lives there and raises the address-error path — the natural home Option (A) of ADR 0003 Decision 2 was chosen to provide.

**Consequences.**
- *Good:* the interrupt seam survives (the positive proof point holds for a third architecture); only the IPL-level nudge is a contract growth, and it is additive. The synchronous-exception bail is the proven fallback valve in M4. `STOP` is free.
- *Bad:* the synchronous mid-instruction vector is a genuinely new control-flow shape the M6 emitter must handle (the alignment check is too pervasive to leave as fallback if the JIT is to be worthwhile). The bus-error extended stack frame (access-info) is more partial code than the 6502's fixed frame.

### Decision 4 — The importer: a field-pattern dataset for the regular ISA; mnemonic-keyed, gzipped TomHarte ingestion

**The problem.** The importer keys opcodes on a single-byte hex regex with 6502 byte-count rules and a flat-per-opcode row model. The 68000 is **field-encoded and extraordinarily regular** (`ADD`, `MOVE`, etc. each fan out across legal sizes × EA-modes × registers from one operation), and — confirmed in ADR 0003 §1.4 — its **TomHarte vectors are mnemonic+size-keyed gzipped files** (`ADD.b.json.gz`), not opcode-hex.

**Decision: the M4 importer schema grows a field-pattern representation for regular ISAs (operation bit-pattern + size field + EA-category), expanded by the generator into descriptors; the TomHarte loader is a NEW mnemonic-keyed, gzip-aware loader.** Concretely:
- The dataset declares the **field grammar once per operation** (mirroring Decision 1's `DecodeStructure` variant) rather than enumerating ~thousands of legal word values. The generator expands the legal `(size × EA × register)` combinations into the keyed descriptor table. This is closer to WinUAE's `table68k` than to `mos6502-opcodes.json` (research §5.2). The single-byte opcode regex and 6502 byte-count rules do not apply — `length` is operand-computed (Decision 1).
- The extraction-as-acceptance-test discipline (ADR 0001 Decision 6) holds: extend the loaders first, cross-source diff two independent 68000 references, then the TomHarte gate. The dataset→importer→generator→regen pipeline stays the law (no hand-edited spec).
- The **TomHarte m68000 loader is structurally new**: it reads gzipped, mnemonic-keyed files (not the 6502/Z80 opcode-hex loaders), parses the `d0–d7`/`a0–a6`/`usp`/`ssp`/`sr`/`pc`/`prefetch`/`ram` state and the `[dir, ?, fc, addr, size, value]` word-granular transaction trace, and reuses the `<cache>/<arch>/v1` directory convention + `Get-test-vectors` script pattern (`TomHarteVectors.cs`). Recommend the cache dir `680x0/v1` (matching the upstream `SingleStepTests/680x0` repo layout) or `m68000/v1`.

**Consequences.**
- *Good:* the field-pattern dataset matches the regular ISA (one `ADD` entry covers all sizes × modes), and aligns with the mnemonic-keyed vectors — the extraction shape and the gate shape agree. Cross-source diff catches the bulk of errors across several-thousand legal encodings.
- *Bad:* a larger importer schema change than the Z80's (which stayed a flat-ish prefixed table). The risk is assuming "it's like the Z80, just bigger" — the *shape* is field-decomposed (research risk 5). The TomHarte loader is net-new code, not a parameterization.

---

## 3. The recommended M4 PR breakdown (the Planner expands PR-by-PR)

Dependency-ordered, each one branch → PR (per the workflow). Mirrors ADR 0001 Decision 8's decomposition style. The JIT hot-op emit is **not** in M4 (it is M6, built once for three ISAs — ADR 0003 Decision 3).

**M4.0 — These ADRs (0003 + 0004).** No code. *(this document + its companion)*

**M4.1 — Core width + state foundation.** `RegisterDef.Bits` → `8|16|32` + `uint` field typing; `FlagBitDef.Bit` → 0–15; the `OperandSize` axis in `Op`/`Spec`; the `USP`/`SSP`/`A7`-banking model + `RegisterRole` decision (ADR 0003 just-in-time item 1); the X-flag/SR layout via `FlagLayout`. Prove the 6502 + Z80 stay byte-identical (`RegeneratedSpec` + their gates). **Depends on:** M4.0. *(Likely splits if the size-axis op-model + the banking each prove large.)*

**M4.2 — Wide big-endian bus.** `Read16/Read32/Write16/Write32` + `Endianness` on `IAddressSpace`; the `AddressSpace` big-endian wide path + fastmem wide load; the even-alignment/address-error hook (the raise wired in M4.5); `TracingAddressSpace` records word/long transactions with size. **Depends on:** M4.0 (parallel with M4.1 — different files).

**M4.3 — Decode + EA framework.** The word-granular field-decomposed `DecodeStructure` variant + the generated word-fetch decode walk (operand-computed length); the structured EA descriptor + EA-compute layer (incl. the auto-inc/dec size-magnitude write-back and the `A7` ±2 case); the EA-category legality matrix replacing the 6502 `switch` + retiring `RequiredIndexRegister`'s `X`/`Y`. Ship the SHAPE + a synthetic field-grammar proof (the M3.1b discipline) before the dataset depends on it. **Depends on:** M4.1.

**M4.4 — Importer field-pattern dataset + the 68000 spec (the extraction acceptance test).** The field-pattern dataset schema; extract + cross-source diff two 68000 references; the new mnemonic-keyed gzip TomHarte loader; emit a generator-clean `M68000Spec.cs`. **Depends on:** M4.3 (the DSL must accept the field grammar + EA modes before the emitter emits them).

**M4.5 — The 68000 interpreter (correctness oracle) + the TomHarte gate.** The hand-written `M68000Cpu` partial (reset reading the initial SSP/PC vectors, the 2-word prefetch queue, supervisor/user mode + `USP`/`SSP` banking, the size-axis op bodies with partial-write/sign-extend/no-CCR-on-`An`, the EA write-back, the exception/vector machinery incl. address error + privilege + TRAP + div-by-zero, the IPL-level interrupt policy, `STOP` via the generic `Halted`); the interpreter bodies for every new micro-op. Gate on the **SingleStepTests 68000 vectors** (per-cycle/per-transaction + `prefetch`). **This is the biggest chunk and will split** — e.g. M4.5a MOVE + integer ALU + the EA modes; M4.5b shift/rotate/bit/BCD + MUL/DIV; M4.5c control flow (`Bcc`/`DBcc`/`BSR`/`JMP`/`JSR`/`RTS`/`RTE`) + `MOVEM`/`LEA`/`LINK`; M4.5d exceptions + privilege + interrupts. **Depends on:** M4.2, M4.4.

**M4.6 — The 68000 through the generic JIT as all-fallback (tier parity).** Wrap in `JittedCpu<M68000Cpu>`, drive the TomHarte-through-JIT sweep, prove byte-identical tier parity (the M3.5-3a pattern; the all-fallback safety valve is the bring-up gate). **No hot-op emit** (that is M6). **Depends on:** M4.5.

**Sequence:**
```
M4.0 (ADRs 0003+0004)
  ├─> M4.1 (Core width + state)  ──┐
  └─> M4.2 (wide BE bus) ──────────┤
        M4.1 └─> M4.3 (decode + EA framework)
                   └─> M4.4 (importer + 68000 dataset/spec)
                         └─> M4.5 (interpreter + TomHarte gate)  [splits a–d]
                               └─> M4.6 (68000 through JIT, all-fallback)
   ───────────────────────────────────────────────────> M6 (cross-arch JIT optimization: hot-op emit, built once for Z80+68000+8086)
```
**Likely to split:** M4.1 (size-axis op-model vs. A7 banking), M4.5 (a–d by instruction family). **Estimated:** ~6 base chunks, realistically ~9 PRs with the splits.

---

## 4. Decisions deliberately left "just-in-time"

1. **The exact field-grammar `DecodeStructure` representation** (mask/match table vs. a richer per-operation record) — settle when M4.3 has real 68000 line-decode encodings in hand from the extraction; over-specifying the record shape before the dataset exists risks a mismatch. The *commitment* (Decision 1: word-granular, field-decomposed, opaque key, operand-computed length) is firm; the record fields are not.
2. **Whether `MOVEM`'s register-mask loop and the BCD/shift-count operands need the extensible operand model in M4.5 or can ride a narrower representation** — depends on what the operand model looks like after ADR 0003 just-in-time item 2 lands. Both `MOVEM` (a one-instruction loop over a bitmask, like the Z80 block ops) and the bit-number/shift-count operands push toward the extensible operand model (ADR 0001 J10); finalize when the first instruction-family PR forces it.
3. **The autovector vs. vectored-interrupt acknowledge detail** (whether the device supplies a vector number or signals autovector) — the IPL-level line (Decision 3) is firm; the acknowledge-cycle detail is a partial-implementation detail best settled against the TomHarte interrupt behavior in M4.5d.

---

## 5. Consequences summary + flags

- **Decode** reuses the post-M3 keyed/computed-length/opaque-key machinery; adds a word-granular field-decomposed variant (the highest-risk M4 abstraction — prove with a synthetic fixture first).
- **Addressing** uses a structured EA descriptor (reusing the Z80 `Indexed` EA-helper), adds the size-magnitude register write-back (first EA side effect), and moves legality to EA-category data (killing the mode mirror-table tax + the `X`/`Y` convention).
- **Exceptions/privilege** keep the generic interrupt seam; the only contract growth is the **IPL-level interrupt line** (additive); the synchronous mid-instruction vector is fallback in M4, an M6 emit design item; `STOP` reuses the generic `Halted`.
- **Importer** grows a field-pattern dataset + a new mnemonic-keyed gzip TomHarte loader matching the confirmed `680x0/v1` vector layout.
- **Reconciliation flags (things in the research brief I corrected against the live tree):**
  - The brief said ADR 0002 "does not exist" and several seams (`FlagLayout`, `DecodeStructure`, the generic JIT) were "in-flight." All have since **merged** — the decode/flag/JIT-genericity pieces the brief treated as pending are **done**, which is why M4's decode decision is "extend the keyed machinery," not "build it."
  - The brief did **not** surface the 2-word **prefetch queue** in the TomHarte gate, nor that the vectors are **mnemonic-keyed gzipped** files with **word-granular `.b/.w/.l` transactions** and **separate `usp`/`ssp` (no `a7`)**. These are confirmed from the upstream data and are load-bearing for the bus decision (ADR 0003 Decision 2), the importer/loader (Decision 4), and the interpreter scope (the prefetch-queue just-in-time item, ADR 0003 §4).
  - **Ambiguity I could not fully resolve:** the exact `transactions` tuple field 2 (the `["r", 4, 6, 3076, ".w", 1657]` second element — likely a cycle-offset or a strobe code) is unconfirmed; M4.2/M4.5 must decode it against the upstream README/format-version note during setup. It does not change any decision here (the size + address + direction + value are clear), but the trace-diff code must interpret it correctly.

---

*End of ADR 0004. The register/bus/size-axis foundation is in the companion ADR 0003. Designer (UX implications: none — this is a headless emulation framework, no UX surface) / Planner can pick up the M4 PR breakdown in §3 from here.*
