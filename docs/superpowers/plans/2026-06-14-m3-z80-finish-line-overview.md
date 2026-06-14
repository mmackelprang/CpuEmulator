# M3.4d–M3.5: The Z80 Finish-Line — Roadmap + PR Breakdown

> **For agentic workers:** this is the OVERVIEW/SEQUENCING doc for finishing the Z80 ISA. It is NOT a
> task-by-task plan. The next slice (the ED block ops 0xA0–0xBB) has a full execution-ready plan in
> `docs/superpowers/plans/2026-06-14-m3-z80-ed-block-ops.md`. The two slices after it are SCOPED here +
> in `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md` and
> `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md`, to be detailed just-in-time before their PR.

**Goal:** finish the Z80 — every documented + undocumented opcode TomHarte-green, the ZEXALL exerciser
passing, interrupt SERVICING implemented, and the Z80 driven through the JIT — in a dependency-ordered set
of reviewable PRs.

**Where we are (PR #22, merge `8b9feab`):** the Z80 **base + CB + ED-core (0x40–0x7F)** planes are
TomHarte-green — 572 covered opcodes, 64k+ cases per the full sweep, registers incl. F's X/Y, I/R, IM,
IFF1/IFF2, WZ, Q, RAM, ports, and the per-T-state bus trace. The WZ/MEMPTR model is COMPLETE with a
universal final Q/WZ/IM check (`checkInternal` retired). Full suite 2252/0/0, `-warnaserror` clean, 6502
byte-identical. This overview was written against that close-state.

---

## 1. What remains, at a glance

| Plane / opcodes | Status today | This-finish-line slice |
|---|---|---|
| Base (unprefixed) | TomHarte-green | done (M3.4a) |
| CB (0x00–0xFF) | TomHarte-green | done (M3.4b) |
| ED core (0x40–0x7F, 64) | TomHarte-green | done (M3.4c, PR #22) |
| **ED block ops (0xA0–0xBB, 16)** | `// TODO(semantics)` skeleton | **M3.4d — the NEXT PR (fully planned)** |
| **DD / FD prefixes ((IX/IY) + (IX+d))** | `// TODO(mode)` (Indexed mode unsupported) | **M3.4e — scoped, likely 2–3 PRs** |
| **DDCB / FDCB compound prefixes** | not decodable (compound key unsupported) | **part of M3.4e** |
| **Interrupt SERVICING (IM 0/1/2 + NMI vectoring)** | `TryServiceInterrupt() => false` | **M3.5** |
| **ZEXALL / ZEXDOC exerciser** | not wired | **M3.5** |
| **Z80 through the JIT** | interpreter-only (ED/CB rows are JIT fallbacks) | **M3.5** |

Total remaining decodable opcodes: **16 ED block + ~252 DD + ~252 FD + 256 DDCB + 256 FDCB ≈ 1,032**, plus
the cross-cutting interrupt-servicing + JIT-genericity work.

---

## 2. Recommended PR breakdown + sequencing

```
PR #22 (DONE) ── base + CB + ED-core green; WZ/MEMPTR complete; universal Q/WZ/IM check
   │
   ▼
M3.4d  ED block ops (0xA0–0xBB)        ← NEXT, fully planned (one PR)
   │      LDI/LDD/LDIR/LDDR, CPI/CPD/CPIR/CPDR, INI/IND/INIR/INDR, OUTI/OUTD/OTIR/OTDR
   │      the repeat-rewind PC quirk, the F3/F5 (X/Y) undocumented-flag quirks, BC/DE/HL auto-inc/dec, WZ
   ▼
M3.4e  DD/FD/DDCB/FDCB IX/IY prefixes  ← scoped; SPLITS into sub-PRs (see §4)
   │      M3.4e-1  framework: the compound-prefix decoder + the Indexed AddrMode + (IX+d) EA
   │      M3.4e-2  DD/FD core (the (IX+d)/(IY+d) re-interpretation of the base + the IX/IY 16-bit ops)
   │      M3.4e-3  DDCB/FDCB compound (bit/rotate/shift on (IX+d), incl. the undoc "store-copy" forms)
   ▼
M3.5   ZEXALL + interrupt servicing + Z80-through-JIT  ← scoped; SPLITS into sub-PRs (see §5)
   │      M3.5-1  interrupt SERVICING (IM 0/1/2 + NMI, IFF1/IFF2, EI-delay, HALT wake)
   │      M3.5-2  ZEXALL/ZEXDOC integration harness (the CP/M BDOS stub + the CRC gate)
   │      M3.5-3  Z80 through the JIT (the J1/J2/J3 generic-compiler work + tier parity) + the findings doc
   ▼
M3.6 (optional)  Z80 host/monitor demo  ── per ADR 0001 Decision 8; not part of "finish the ISA"
```

**Why this order:**

1. **ED block ops FIRST** because they are the smallest remaining slice, their dataset rows already exist
   (no large F1 gap — see §3), they need NO new AddrMode or decoder change, and they are the natural
   continuation of the ED plane just shipped. They also introduce the **repeat-rewind** primitive (an
   instruction that does not advance PC) the JIT block model will later have to reason about — getting it
   correct in the interpreter first is the right sequence (ADR 0001 Decision 4: "a block op is a
   one-instruction loop").
2. **DD/FD/DDCB/FDCB SECOND** because it is the last and largest decode/addressing reshaping — it forces the
   `Indexed` AddrMode, the `(IX+d)` EA computation, AND the two-deep **compound-prefix decoder** (the
   `DD CB dd op` displacement-before-opcode form the current single-byte/single-prefix decode walk cannot
   express, ADR 0001 Decision 1). With it, the interpreter covers the **entire** documented + undocumented
   Z80 ISA — the precondition for ZEXALL.
3. **ZEXALL + interrupt servicing + JIT LAST** because ZEXALL exercises the *whole* ISA (it cannot pass
   until DD/FD land), interrupt servicing is not single-step-vector-testable (it needs an integration gate
   like ZEXALL or a dedicated interrupt UAT), and the JIT-genericity work (ADR 0001 Decision 7, J1/J2/J3) is
   the deliverable whose *input* is a fully-correct interpreter across every plane.

**Dependency summary:** `M3.4d → M3.4e → M3.5`. Within M3.4e: `e-1 (framework) → e-2 (DD/FD core) → e-3
(DDCB/FDCB)`. Within M3.5: `5-1 (servicing) ∥ 5-2 (ZEXALL harness)` can proceed in parallel after M3.4e,
but ZEXALL's full pass depends on servicing for its interrupt-using sub-tests; `5-3 (JIT)` depends on the
interpreter being complete (after M3.4e) and benefits from ZEXALL as an integration oracle.

---

## 3. Vector availability per slice (CONFIRMED against `~/.cache/cpuemulator/vectors/z80/v1/`)

The cache holds **1,604** vector files (SingleStepTests/z80 v1; filenames contain a SPACE). Per-slice:

| Slice | Vector files | Filename pattern | Confirmed |
|---|---|---|---|
| ED block ops | **16** | `ed a0.json` … `ed bb.json` (a0–a3, a8–ab, b0–b3, b8–bb) | YES — all 16 present |
| DD prefix | **252** | `dd 00.json` … `dd ff.json` minus the 4 prefix bytes | YES — 252 present (missing only `dd cb`/`dd dd`/`dd ed`/`dd fd`, which are prefix-chains, not standalone ops) |
| FD prefix | **252** | `fd 00.json` … `fd ff.json` minus the 4 prefix bytes | YES — 252 present (same 4 prefix-chain gaps) |
| DDCB compound | **256** | `dd cb __ 00.json` … `dd cb __ ff.json` | YES — 256 present (the `__` is the displacement-byte placeholder; the final opcode byte is the last token) |
| FDCB compound | **256** | `fd cb __ 00.json` … `fd cb __ ff.json` | YES — 256 present |

**Vector-naming finding (load-bearing for M3.4e):** the compound vectors are named **`dd cb __ NN.json`** —
FOUR space-separated tokens, where `__` is a literal two-underscore placeholder for the displacement byte
and `NN` is the FINAL opcode byte (the byte AFTER the displacement). The DD/FD theories build
`$"dd {op:x2}.json"`; the DDCB/FDCB theories must build `$"dd cb __ {op:x2}.json"` (note: the displacement
is `__` regardless — the vector file's `initial.ram` carries the actual displacement byte). The harness
must NOT assume a 3-token `dd cb NN.json` form.

**ZEXALL (M3.5):** ZEXALL/ZEXDOC are NOT in the SingleStepTests vector cache — they are a separate artifact
(a `.com` CP/M binary + expected CRCs). M3.5-2 fetches/embeds them (see that slice's scoping).

**The recurring F1 risk (dataset rows vs vectors) — checked per slice:**

The M3.4c F1 finding was that `z80-opcodes.json` was missing 22 of 64 ED-core rows. Re-checked for every
remaining slice (counts from `z80-opcodes.json`, total 728 rows):

| Slice | Dataset rows present | Vectors | F1 gap? |
|---|---|---|---|
| ED block ops | **16** (LDI…OTDR, all present at lines 5470–5629, `mode: Implied`, `bytes: 2`, `cycles: 16`) | 16 | **NO gap** — all 16 dataset rows exist; the slice needs SEMANTICS + emitter, not dataset rows. |
| DD prefix | **39** | 252 | **LARGE gap** — ~213 DD rows missing (the `(IX+d)` re-interpretations of base ops, deferred as `// TODO(mode)`). M3.4e-2 must add them. |
| FD prefix | **39** | 252 | **LARGE gap** — ~213 FD rows missing. M3.4e-2 must add them. |
| DDCB compound | **31** | 256 | **LARGE gap** — ~225 DDCB rows missing. M3.4e-3 must add them. |
| FDCB compound | **31** | 256 | **LARGE gap** — ~225 FDCB rows missing. M3.4e-3 must add them. |

So: **ED block ops have NO dataset gap** (the easy slice); **DD/FD/DDCB/FDCB have a ~876-row dataset gap**
(the hard slice) — exactly the M3.4c F1 lesson at much larger scale. M3.4e must add the missing rows
algorithmically (the DD/FD rows are a mechanical re-interpretation of the base/CB tables — see §4), not by
hand, and cross-check the dataset count against the 1,016 DD+FD+DDCB+FDCB vectors before claiming coverage.

---

## 4. M3.4e (DD/FD/DDCB/FDCB) — scoping + the open design decisions

**What it is.** The DD/FD prefixes re-interpret `HL` as `IX`/`IY`. A DD/FD prefix on a base opcode that
touches H/L/(HL) instead touches IXh/IXl/(IX+d) (with a displacement byte d for the indirect forms). The
compound `DD CB dd op` form puts the displacement byte BEFORE the final opcode byte — a decode shape no
single-byte/single-prefix decoder expresses. This is ADR 0001 Decision 1 (the central decode decision) and
Decision 3 (IX/IY as registers — already declared in `z80-semantics.json:25-26` as 16-bit).

**Recommended split (2–3 PRs):**

- **M3.4e-1 — framework: the compound-prefix decoder + `Indexed` AddrMode + `(IX+d)` EA.**
  - Add `Indexed` to `AddrMode` (`AddrMode.cs`) + the mirror tables (`SpecParser.cs:162-172 s_addrModes`,
    `SpecFileEmitter.cs:49-60 SupportedModes`, the per-class mode sets, the JIT `JitMode` mirror).
  - Extend `DecodeStructure`/`PrefixByte` (`DecodeStructure.cs`) so a prefix can declare "takes a leading
    displacement byte before the opcode" (the `DD CB dd op` shape) — the compound-prefix flag the current
    `PrefixByte(byte Value)` lacks.
  - Extend `EmitStructuredDecodeWalk` (`CpuEmitter.cs:3322-3384`) to handle a SECOND prefix byte and the
    compound displacement: today it does `if (s_prefixBytes.Contains(first)) { op = next; key = (first<<8)|op; }`
    — it must additionally handle `first == 0xDD/0xFD` then `second == 0xCB` (read displacement, THEN
    opcode → a compound key + a displacement local), and `first == 0xDD/0xFD` then a normal opcode
    (→ `(0xDD<<8)|op` with HL→IX substitution). Prove the 6502 + existing Z80 planes regenerate
    byte-identically (zero-prefix + single-prefix are the unchanged special cases).
  - Add the `(IX+d)` EA computation to the emit arms (read IX/IY, add the signed displacement byte).
  - Gate: the framework change alone (no DD/FD rows live yet) keeps base+CB+ED green + 6502 byte-identical.

- **M3.4e-2 — DD/FD core.** Add the ~213+213 missing DD/FD dataset rows (algorithmically — a DD row is the
  base row with H→IXh, L→IXl, (HL)→(IX+d); the importer can derive them from the base table the way
  `Z80BaseSemantics` derives base ops from the octal fields). The IX/IY 16-bit ops (ADD IX,rr; INC/DEC IX;
  LD IX,nn; LD (nn),IX; PUSH/POP IX; JP (IX); EX (SP),IX). Drive `dd *.json` + `fd *.json` (504 vectors)
  green. The undocumented IXh/IXl 8-bit ops (the DD-prefixed `LD B,IXh` etc.) are IN scope — the vectors
  check them.

- **M3.4e-3 — DDCB/FDCB compound.** The bit/rotate/shift ops on `(IX+d)`, INCLUDING the **undocumented
  "store-copy" forms** (a DDCB op with a register field ≠ 6 does the operation on `(IX+d)` AND copies the
  result into the named register — e.g. `DD CB d 00` = `RLC (IX+d) → B`). All 512 DDCB+FDCB vectors. This
  is the densest undocumented-behavior slice in the Z80.

**OPEN DESIGN DECISIONS for M3.4e (need a human/Coordinator call before the e-1 Builder run):**

- **(D1) Compound-prefix decode model.** ADR 0001 Decision 1 recommended option (A) "nested prefix tables +
  a generated decode walk." The concrete question for the IMPLEMENTATION: does `PrefixByte` gain a
  `bool LeadingDisplacement` (or a `CompoundWith` byte) so the spec DECLARES `DD CB` takes a displacement
  before the opcode, and `EmitStructuredDecodeWalk` reads it generically? Or do we special-case
  `0xDD/0xFD + 0xCB` in the walk (cheaper now, less generic — pays the 8086 cost later, ADR risk-Q2)?
  **Recommendation:** the declarative `PrefixByte` extension (matches ADR Decision 1's "the spec declares
  its decode structure" thesis and the cross-arch optimization goal), but confirm the appetite for the
  larger schema delta.
- **(D2) `(IX+d)` as a new AddrMode vs. a parameter on existing modes.** The ADR's mode table lists
  `Indexed` as a distinct mode. The question: is `Indexed` ONE new AddrMode used by every DD/FD indirect
  row (cleanest), or does each existing class (Load/Store/Alu/Bit) gain an "indexed variant" flag?
  **Recommendation:** a single new `Indexed` AddrMode (the ADR's framing; one mirror-table edit per table)
  with the displacement carried as the operand byte — but the class/mode matrix
  (`ValidateModeForClass`) must then allow `Indexed` for Load/Store/Alu/Rot/Bit, which is a broad matrix
  change (ADR Decision 4: "expect the class/mode matrix to be substantially rebuilt").
- **(D3) DD/FD dataset-row generation.** Hand-authoring ~876 rows is the F1 risk at scale. Should the
  importer DERIVE the DD/FD rows from the base/CB tables (a `Z80DdFdSemantics` that maps base op N → its
  IX-substituted form), with the dataset carrying only the IX/IY-SPECIFIC rows (the 16-bit ops + the
  undoc IXh/IXl)? **Recommendation:** derive algorithmically (consistent with `Z80BaseSemantics`/
  `Z80CbSemantics`/`Z80EdSemantics`), so the dataset stays small and the F1 gap closes by construction —
  but this is a non-trivial importer design the e-1 PR should settle.
- **(D4) JIT treatment of (IX+d) + DDCB.** Per ADR Decision 4/7, the safe path emits the hot straight-line
  DD/FD ops and FALLS BACK for the compound DDCB/FDCB. Confirm DDCB/FDCB are fallback-only in M3.4e (their
  IL emission is M3.5/post-M3). **Recommendation:** fallback-only for DDCB/FDCB in M3.4e; revisit in M3.5.

---

## 5. M3.5 (ZEXALL + interrupt servicing + JIT) — scoping + the open design decisions

**What it is.** Three coupled deliverables that together close the Z80:

- **M3.5-1 — interrupt SERVICING.** Implement the partial's `TryServiceInterrupt()` (currently `=> false`,
  `Z80Cpu.cs:124`) and `InterruptPending` (currently `=> false`, `Z80Cpu.cs:78`): IM 0 (device supplies an
  opcode, usually RST n), IM 1 (fixed RST 38h → 0x0038), IM 2 (vectored: `I<<8 | device-byte` → table
  pointer), NMI (fixed 0x0066, saves IFF1→IFF2, clears IFF1), the EI one-instruction-delay latch, and the
  HALT wake (the `_halted` latch + `Halted` partial already exist, `Z80Cpu.cs:23,82`; servicing clears it).
  ADR 0001 Decision 5 recommends keeping the hand-written-partial seam (option A) — this is the seam the
  ADR predicted "should survive Z80 unchanged"; M3.5-1 confirms or refutes that.
- **M3.5-2 — ZEXALL/ZEXDOC.** The exhaustive exerciser: a CP/M BDOS stub (functions 2 + 9 for console
  output) + the ZEXALL `.com` loaded at 0x0100, run to completion, CRCs compared to the known-good set.
  ZEXDOC (documented-flags) vs ZEXALL (all flags incl. X/Y) — run both; ZEXALL is the stricter gate and the
  undocumented-flag finish-line proof.
- **M3.5-3 — Z80 through the JIT.** The ADR Decision 7 / J1/J2/J3 generic-compiler work: make
  `BlockCompiler` generic over the CPU type (today `private readonly Mos6502Cpu _cpu;`,
  `BlockCompiler.cs:16`), the register file data-driven (today A/P/PC are baked `FieldInfo`s,
  `BlockCompiler.cs:52-54`; operand regs already go through `_regFields`), and the decode per-page (the
  structured CPU already emits `JitDescriptorsByKey`, `CpuEmitter.cs:3002` — J3 is partly done). Emit the
  hot straight-line Z80 ops, fall back for block ops/DAA/EX/(IX+d)/DDCB/I-O, prove tier parity (the
  differential fuzzer + the TomHarte sweep through the JIT, as M2-ii did for the 6502). Deliver the
  enumerated Decision-7 findings as the headline artifact.

**Why M3.5 is "scoped, to be detailed just-in-time":** the deepest literal-code detail GENUINELY depends on
what M3.4e reveals. The JIT-genericity work (J1/J2) cannot be written task-by-task with literal IL until we
know exactly which DD/FD/DDCB ops are hot enough to emit vs. fall back — and that ranking comes from running
M3.4e's interpreter under ZEXALL. The interrupt-servicing detail depends on whether M3.4e's decode changes
touched the `Step` fetch path. Pinning literal code now would be fabricated precision. The honest move is to
detail M3.5's PRs after M3.4e merges.

**OPEN DESIGN DECISIONS for M3.5 (need a human/Coordinator call before the respective Builder run):**

- **(D5) Interrupt-servicing TEST gate.** Servicing is NOT single-step-vector-testable (ADR Decision 5;
  M3.4c explicitly deferred it). The gate must be EITHER (a) ZEXALL's interrupt-using sub-tests, (b) a
  dedicated hand-written interrupt UAT (assert PC/SP/IFF after an injected IRQ/NMI in each IM mode), or
  (c) both. **Recommendation:** a dedicated interrupt UAT (deterministic, debuggable) as the primary gate +
  ZEXALL as the integration confirmation. Confirm before M3.5-1.
- **(D6) ZEXALL CP/M-host shape.** ZEXALL needs a minimal CP/M BDOS (console out) + a `.com` loader. Does
  this live as a TEST fixture (a throwaway `ZexallHarness` in the test project) or as a real
  `CpuEmulator.Hosts.CpmZ80` machine (which would also serve the optional M3.6 demo)? **Recommendation:**
  a test fixture for M3.5 (YAGNI — don't build the M3.6 host early); promote to a real host only if M3.6 is
  greenlit. Confirm before M3.5-2.
- **(D7) JIT line — how far to push Z80-through-JIT in M3.5.** ADR risk-Q3 ("where is the M3 line?") is
  still open. The safe path: emit hot straight-line ops, fall back for the irregular ones (block ops, DAA,
  EX (SP),HL, (IX+d), DDCB, I/O, interrupt servicing). Full Z80 JIT emission (block-op loops in IL, DAA in
  IL) arguably belongs in the POST-M3 cross-arch optimization milestone. **Recommendation:** hot-ops-only +
  fallbacks for M3.5; the findings doc enumerates what was left as fallback and why. Confirm the line before
  M3.5-3.
- **(D8) ZEXALL undocumented-flag bar.** ADR risk-Q4. ZEXDOC (documented flags) is a softer gate; ZEXALL
  (all flags incl. X/Y) is the strict one. Since the TomHarte sweeps ALREADY enforce X/Y per-instruction,
  ZEXALL should pass — **recommendation: gate on ZEXALL (the strict form)**, with ZEXDOC as a
  faster-feedback pre-check. Confirm.

---

## 6. Invariants every slice's plan must carry forward

(From the M3.4c plan + the task brief — these are non-negotiable for each PR.)

- **TDD task-by-task.** Failing test first; full gate after each task.
- **Full gate after each task:** `dotnet build --no-incremental -warnaserror` clean; targeted tests green;
  the 6502 byte-identity guard `RegeneratedSpecTests` green; base/CB/ED planes stay green at the universal
  Q/WZ/IM bar.
- **Every 6502 artifact byte-identical.** The dataset→importer→regen→generator pipeline only — never
  hand-edit `Z80Spec.cs`.
- **The honest close-state ethos.** Each plan ends with EXACTLY what is and isn't covered, enumerated. Never
  overstate (the M3.3 honesty lesson; the M3.4c closeout is the template).
- **The synthetic-spec test pattern** (`GeneratorTestHost.CompileAndLoadType`) decouples per-task tests from
  the real `Z80Spec.cs` regen, which lands atomically late in each slice — exactly as the CB + ED plans did.
  Note the M3.4c deviation #1: structured synthetic fixtures use `IAddressSpace _bus` (not a raw `byte[]`),
  and declare `public byte Q;` + `public int Im;`.

---

## 7. The honest finish-line one-liner (what "done" means)

When all three slices land: the Z80 base + CB + ED (core + block) + DD + FD + DDCB + FDCB planes are
TomHarte-green (every documented + undocumented opcode, per-T-state); ZEXALL passes (the integration proof);
interrupt servicing works for IM 0/1/2 + NMI with a dedicated interrupt UAT; and the Z80 runs through a
JIT that is now generic over the CPU type / register file / decode structure (the ADR Decision-7 findings
enumerated), with the hot ops emitted and the irregular ones as proven fallbacks. The cross-architecture
JIT OPTIMIZATION remains a separate POST-M3 milestone (gated, per the 2026-06-13 human checkpoint, behind
M4 68000 + M5 8086 — NOT part of this finish-line).

---

## 8. Slice docs index

- **M3.4d (NEXT, fully planned):** `docs/superpowers/plans/2026-06-14-m3-z80-ed-block-ops.md`
- **M3.4e (scoped):** `docs/superpowers/plans/2026-06-14-m3-z80-ixiy-prefixes.md`
- **M3.5 (scoped):** `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md`
- **Predecessor (the depth template):** `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`
- **Architecture record:** `docs/architecture/0001-z80-second-architecture.md`
