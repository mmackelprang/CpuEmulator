# M3.5-1: Z80 Interrupt Servicing — IM 0/1/2 + NMI + IFF1/IFF2 + the EI-delay + the HALT wake

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or
> superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for
> tracking. Depth template: `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`.

**Goal:** implement the Z80 interrupt POLICY — fill the partial's `TryServiceInterrupt()` + `InterruptPending`
stubs (currently `=> false`) so the CPU responds to a maskable IRQ in IM 0/1/2 and to an NMI, honoring
IFF1/IFF2 (including the `EI` one-instruction delay) and the HALT wake — gated by a dedicated, deterministic,
hand-written interrupt UAT (decision D5). The TomHarte single-step path stays UNCHANGED (servicing only fires
between instructions, driven by the UAT/host, never the vector runner).

**Architecture:** ADR 0001 Decision 5 option A — **no `Core`/generator change**. The interrupt seam was built
CPU-agnostic in M1 and confirmed by M3.4: the generated `Step()` already calls `TryServiceInterrupt()` before
the opcode fetch and idles on `Halted` only after that hook (`CpuEmitter.cs:147-162`); the JIT samples
`InterruptPending` at block boundaries. M3.5-1 implements the policy ENTIRELY in the hand-written partial
`src/CpuEmulator.Cpus.Z80/Z80Cpu.cs`, mirroring the 6502's partial (`Mos6502Cpu.cs:54-107`): an edge-latched
NMI line, a level-sensitive IRQ line, an `EI`-delay latch, and a `TryServiceInterrupt()` that performs the
real push/vector bus sequence (charging cycles via `ReadBus`/`WriteBus` + explicit internal `_cycles +=`),
bumps R for the M1 acknowledge, clears `_halted` (the wake), and returns `true`. The one generated-side touch
is the `EI` micro-op body, which today sets `_iff1/_iff2` IMMEDIATELY (`CpuEmitter.cs:2020-2022`) with no
delay — M3.5-1 routes it through a partial hook so the delay latch lives in the partial.

**Tech Stack:** C# (.NET 10); a Roslyn incremental source generator (`CpuEmulator.Generators`); the Z80
hand-written partial (`CpuEmulator.Cpus.Z80`); xUnit (the interrupt UAT is a new inline-fixture test class
mirroring `Z80TomHarteRunnerSelfTests.cs` — no live vectors). ZEXALL (M3.5-2) is the integration confirmation,
NOT this PR.

---

## Scope

**IN scope (interrupt SERVICING — the partial fills the stubs):**

1. **`InterruptPending`** → `_nmiPending || (_irqLine && _iff1)`. NMI is non-maskable; INT is gated by IFF1.
2. **`TryServiceInterrupt()`** — the boundary-sampled policy:
   - **NMI** (highest priority): push PC (2 writes), `IFF2 := IFF1` (saved), `IFF1 := 0`, `PC := 0x0066`,
     `WZ := 0x0066`, clear `_halted`, bump R by 1, charge **11 T**. RETN later restores `IFF1 := IFF2`
     (already emitted — `EmitZ80EdRetn`, `CpuEmitter.cs:3033`).
   - **IM 0** (maskable): the device supplies an opcode on the data bus — model the **common `RST n` form**
     (the canonical IM 0 use). Push PC, `PC := n` (the RST vector, default `0x0038` if no device byte is
     configured), `WZ := n`, `IFF1 := IFF2 := 0`, clear `_halted`, bump R by 1, charge **13 T**.
   - **IM 1** (maskable): push PC, `PC := 0x0038` (fixed RST 38h), `WZ := 0x0038`, `IFF1 := IFF2 := 0`,
     clear `_halted`, bump R by 1, charge **13 T**.
   - **IM 2** (maskable): read the device byte `vec`; form `addr = (ushort)((I << 8) | (vec & 0xFE))`; read
     the 16-bit vector from `addr` (lo then hi, 2 bus reads); push PC; `PC := vector`; `WZ := vector`;
     `IFF1 := IFF2 := 0`; clear `_halted`; bump R by 1; charge **19 T**.
3. **The IFF1/IFF2 gate semantics** — maskable IRQ requires `IFF1`; acknowledge clears both IFF1 and IFF2.
   NMI clears only IFF1 (saving it into IFF2). `Iff1`/`Iff2` are already settable (`Z80Cpu.cs:32-35`).
4. **The `EI` one-instruction delay** — `EI` enables interrupts only AFTER the instruction FOLLOWING it (a
   documented Z80 quirk). The partial holds an `_eiPending` latch: the `EI` op body sets it; the boundary
   logic commits `_iff1 = _iff2 = true` one instruction later and only THEN allows servicing. This requires
   routing the generated `Ei` micro-op body through a partial hook (the ONLY generator touch).
5. **The HALT wake** — `TryServiceInterrupt()` clears `_halted` when it services. The generated `Step` already
   idles one cycle while halted (after the hook); the `Run` loop burns budget one cycle per `Step` — correct
   Z80 HALT behavior, no busy-spin pathology (it consumes the cycle budget, it does not loop forever).
6. **The line setters** — `SetIrqLine(bool)` (level-sensitive: store the level) and `SetNmiLine(bool)`
   (edge-triggered: a rising edge sets `_nmiPending`), mirroring the 6502 (`Mos6502Cpu.cs:54-64`).
7. **A device-byte hook for IM 0 / IM 2** — a settable `InterruptData` byte (the value the device places on
   the bus during the acknowledge), defaulting such that IM 0 → RST 38h and IM 2 reads its table index from
   it. This is host/UAT-driven state, NOT a bus read in the test harness (keeps the UAT deterministic).

**The TEST gate (D5):** a dedicated, deterministic interrupt UAT (`Z80InterruptServicingTests`, a new xUnit
class mirroring `Z80TomHarteRunnerSelfTests`): construct a `Z80Cpu`, set IM mode + IFF state + memory + a
pending IRQ/NMI line, `Step()` (which services), and assert the serviced vector PC, the pushed return
address, IFF1/IFF2 after, the cycle cost, R, WZ, and the HALT-wake. Cover: IM0 RST, IM1 (0x38), IM2
(I-table vector), NMI (0x66 + RETN restore), the EI-delay (no fire immediately after EI), the DI mask, and
HALT-then-IRQ wake. ZEXALL (M3.5-2) is the integration confirmation.

**OUT of scope (each is later / separate):**

- **ZEXALL/ZEXDOC** — M3.5-2 (the integration exerciser; ZEXALL itself does not use interrupts, so it does
  not gate this PR; see the scoped plan `…-zexall-jit-m35.md`).
- **The Z80 through the JIT** — M3.5-3. Servicing is a JIT FALLBACK (the JIT boundary-samples
  `InterruptPending` and routes through the interpreter `Step` when pending — already the seam). No IL is
  emitted for servicing.
- **Cycle-exact per-T-state BUS TRACE of the acknowledge sequence.** The interrupt UAT asserts the cycle
  COUNT (T-state total) + the register/memory/IFF effects, NOT a per-T-state MREQ trace (the SingleStepTests
  vectors do not cover servicing, so there is no oracle trace to match). The push writes are real and ordered
  (so RAM is correct); the internal M1-acknowledge T-states are charged as a lump via `_cycles +=`. This
  matches how the 6502 partial charges its reset/service internals coarsely (`Mos6502Cpu.cs:45,99-100`) — a
  recorded, consistent deviation, NOT new.
- **The daisy-chain / RETI device-acknowledge protocol.** RETI is modeled as RETN (both restore IFF1←IFF2);
  the Z80 PIO/CTC daisy-chain ack is host-peripheral behavior, out of scope (and untested by any vector).
- **Mid-instruction interrupt sampling / the exact EI-then-DI race edge cases** beyond the one-instruction
  EI delay. The Z80 samples INT at the END of an instruction; the EI-delay is the one documented quirk we
  model. Finer sampling races (e.g. interrupt during a block-op repeat) are noted in Risks, not implemented
  (the block-op self-repeat does not advance PC, so it is a single `Step`; servicing fires between repeats
  only if the block op completes — acceptable and documented).

> **The honest one-liner for M3.5-1's close-state:** the Z80 services a maskable IRQ in IM 0 (common RST-n
> form) / IM 1 (RST 38h) / IM 2 (I-table vector) and an NMI (0x66, IFF saved/restored via RETN), honoring
> IFF1/IFF2 and the EI one-instruction delay, and wakes from HALT — gated by a deterministic interrupt UAT
> (IM0/IM1/IM2/NMI/EI-delay/DI-mask/HALT-wake). The whole Z80 TomHarte sweep (base→FDCB) stays green at the
> universal Q/WZ/IM bar — servicing does NOT perturb the single-step path (`TryServiceInterrupt` returns
> false when nothing is pending, so every TomHarte case is unaffected). The 6502 is byte-identical (the
> only generator touch — the `Ei`-delay partial hook — is guarded `structured`, so the 6502 `.g.cs` is
> unchanged). ZEXALL (integration) is M3.5-2; the JIT is M3.5-3. The ADR's "the interrupt seam should
> survive Z80 unchanged" hypothesis is CONFIRMED for `Core`/the generated dispatcher; the ONE enumerated
> generator delta is the `Ei`-body partial hook (a micro-op body change, not a seam change) — recorded
> honestly.

---

## Ground truth — the seams M3.5-1 fills (CONFIRMED by recon, re-confirm at Task 0)

**Confirm each by reading the cited file:line at Task 0** — the implementation REUSES them.

- **The generated `Step` already calls the hook + idles on halt (the seam is exactly as predicted).**
  `src/CpuEmulator.Generators/CpuEmitter.cs:147-148`: `if (TryServiceInterrupt()) return;` runs FIRST; then
  (`:155-162`) `if (Halted) { IdleCycle(); return; }` — the comment at `:150-152` states "TryServiceInterrupt
  ran first (it clears the latch on a serviced interrupt — the wake), so when still halted, idle exactly one
  cycle." **So clearing `_halted` inside `TryServiceInterrupt` IS the wake — no generated change needed for
  HALT.** The 6502 (Decode null, no HaltOp) takes neither line — byte-identical.
- **The partial is the stub, shaped for this.** `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs`:
  `public partial bool InterruptPending => false;` (`:78`), `private partial bool TryServiceInterrupt() =>
  false;` (`:124`), `public void SetIrqLine(bool asserted) { }` / `public void SetNmiLine(bool asserted) { }`
  (`:74-75`), the `_iff1`/`_iff2` latches + `Iff1`/`Iff2` props (`:27-35`), `public int Im;` (`:47`), the
  `_halted` latch + `Halted` partial + `DoHalt()` + `IdleCycle()` (`:23,82,114,117`), `ReadBus`/`WriteBus`
  (charge one cycle each, `:85-96`), `ReadIo`/`WriteIo` (`:100-111`), the R-refresh `OnInstructionFetched`
  (`:130-134` — `R = (byte)((R & 0x80) | ((R + 1) & 0x7F))` per fetch). `Reset()` (`:61-70`) clears
  `_iff1/_iff2/_halted`.
- **The 6502 partial is the exact precedent.** `src/CpuEmulator.Cpus.Mos6502/Mos6502Cpu.cs`:
  `_irqLine`/`_nmiLine`/`_nmiPending` fields (`:22-24`); `SetIrqLine` stores the level (`:54`); `SetNmiLine`
  latches a rising edge (`:59-64`); `InterruptPending => _nmiPending || (_irqLine && (P & 0x04) == 0)`
  (`:69`); `TryServiceInterrupt()` (`:85-107`) clears the NMI latch, does the push sequence via
  `WriteBus`/`ReadBus`, fetches the vector, returns true. **M3.5-1 writes the Z80 analog of this method.**
  Note the 6502 charges its dummy/internal cycles by the real bus reads (`:93-94`) — the Z80 charges its
  M1-acknowledge internal T-states by an explicit `_cycles +=` (no bus read for the internal portion).
- **The `EI` body sets IFF IMMEDIATELY today — no delay latch (THIS is the one generator touch).**
  `CpuEmitter.cs:2017-2022`: `case "Di": _iff1 = false; _iff2 = false;` / `case "Ei": _iff1 = true; _iff2 =
  true;`. The M3.4a model is flag-state-correct for the TomHarte vectors (which set `iff1`/`iff2` in the
  final state and do NOT test servicing), but it does NOT model the one-instruction delay. **M3.5-1 routes
  `Ei` through a partial hook `OnInterruptEnable()`** so the partial holds the delay latch (the `Di` body
  stays immediate — `DI` has no delay). See the genericity note in Task 6.
- **`RETN`/`RETI` already restore IFF1←IFF2 (the NMI-return path).** `EmitZ80EdRetn` (`CpuEmitter.cs:3023-
  3035`) emits `_iff1 = _iff2;` after popping PC. So after an NMI (which did `_iff2 = _iff1; _iff1 = 0`), a
  `RETN` restores `_iff1 = _iff2` — the saved value. No change needed; the UAT asserts the round-trip.
- **The cycle property.** `CpuEmitter.cs:50-51`: `private long _cycles; public long CycleCount => _cycles;`.
  `ReadBus`/`WriteBus` each `_cycles++`. Servicing charges: (bus pushes/reads via ReadBus/WriteBus) + (the
  remaining M1-acknowledge internal T-states via an explicit `_cycles +=`).
- **The runner self-test pattern is the UAT template.**
  `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunnerSelfTests.cs` — inline `[Fact]`s, no live vectors,
  constructs state and asserts. The interrupt UAT mirrors it BUT constructs the `Z80Cpu` directly (no JSON
  case) since there is no servicing vector. The `Z80Cpu` public API the UAT uses: the two-bus ctor
  `new Z80Cpu(bus, io)`, `SetRegister`/`GetRegister`, `Iff1`/`Iff2`/`Im`/`Q` (settable), `CycleCount`,
  `SetIrqLine`/`SetNmiLine`, `Step()`, `Reset()` (see `Z80TomHarteRunner.cs:38-54` for the construction
  shape and the `AddressSpace`/`TracingAddressSpace` wiring).

### RECON FINDINGS that refine the prose (re-confirm at Task 0; the cited code WINS)

- **R1 — The HALT wake needs NO generated change.** The scoped plan flagged a possible `Run` busy-spin
  (ADR risk-Q8). Recon confirms `Step` idles one cycle on `Halted` AFTER `TryServiceInterrupt`
  (`CpuEmitter.cs:155-162`), and `Run` (`:250-258`) decrements the cycle budget by the cycles each `Step`
  charges. A halted CPU therefore burns one cycle of budget per `Step` and `Run` terminates when the budget
  is exhausted — it does NOT loop forever. Clearing `_halted` in `TryServiceInterrupt` resumes fetch on the
  next `Step`. **No `Run`/generator change is needed for HALT — a POSITIVE genericity finding (refutes the
  risk-Q8 watch-item for the interpreter; the JIT dispatcher's halted fast path is M3.5-3's concern).**
- **R2 — The `Ei`-delay is the ONE generator touch and it is a micro-op body change, not a seam change.**
  The interrupt SEAM (`TryServiceInterrupt`/`InterruptPending`/`Step`'s call site) survives Z80 unchanged
  (ADR Decision 5 confirmed). The `Ei` body change is in the OP VOCABULARY (`EmitZ80Misc`'s `Ei` arm),
  guarded by the structured/partial path so the 6502 is byte-identical. Enumerate it honestly: "the seam is
  generic; the `EI`-delay is a Z80 op-semantics detail that needs the partial to own the latch."
- **R3 — The interrupt UAT cannot reuse `Z80TomHarteRunner`.** That runner loads a `Z80TomHarteCase` (a
  single-step vector) and `Step`s once expecting an instruction, not a service. The UAT constructs the CPU
  directly. Build a small local helper in the UAT class (`BuildCpu(out bus, out io)`) mirroring
  `Z80TomHarteRunner.RunCase`'s construction (`:26-38`).
- **R4 — `InterruptData` (the IM 0 / IM 2 device byte) has no existing field.** Add a settable
  `public byte InterruptData { get; set; }` (default `0xFF`, so IM 0's RST decode → `0xFF & 0x38`? NO — see
  Task 3: IM 0 default models RST 38h directly; IM 2 uses `InterruptData` as the table-index low byte). The
  UAT sets it per case. This is host-driven state, like `Iff1`/`Im`.
- **R5 — The pushed return address is the CURRENT PC (the instruction that WOULD have run next).** Servicing
  fires at an instruction boundary BEFORE the fetch, so PC already points at the next instruction; push that
  PC unchanged (the Z80 resumes there on RET/RETN). Confirm against the standard Z80 model: yes — the maskable
  INT and NMI both push the address of the next instruction. (Contrast the 6502, identical: it pushes PC
  before the would-be-fetched opcode.)
- **R6 — R bumps by 1 per acknowledge.** The interrupt-acknowledge cycle IS an M1 cycle (the Z80 asserts
  M1+IORQ together). So R's low 7 bits increment by 1 on service, exactly as a one-byte opcode fetch would
  (`OnInstructionFetched(1)` semantics). The UAT pins R before/after. Apply the SAME refresh formula the
  partial already uses (`R = (byte)((R & 0x80) | ((R + 1) & 0x7F))`).

---

## The pinned cycle / R / IFF / WZ rules (with sources)

> Re-derive carefully at implementation time. Sources: the Zilog *Z80 CPU User Manual* (UM008,
> §6 "Interrupt Response"), the standard community Z80 timing tables (z80.info / "The Undocumented Z80
> Documented" §5.4), and cross-checked against MAME's `z80.cpp` `take_interrupt()` / `nmi()`. These are
> the documented machine-cycle totals; the UAT pins them as the cycle-count oracle.

| Event | Sequence | T-states | R bump | IFF1 after | IFF2 after | PC after | WZ after |
|---|---|---|---|---|---|---|---|
| **NMI** | M1 ack (5T) + push PCH (3T) + push PCL (3T) | **11** | +1 | 0 (saved into IFF2) | = old IFF1 | 0x0066 | 0x0066 |
| **IM 0 (RST n)** | M1 ack (5T) + 2 internal (2T... → modeled as the RST n exec) + push PCH (3T) + push PCL (3T) | **13** | +1 | 0 | 0 | n (default 0x0038) | n |
| **IM 1 (RST 38h)** | M1 ack (7T incl. 2 internal) + push PCH (3T) + push PCL (3T) | **13** | +1 | 0 | 0 | 0x0038 | 0x0038 |
| **IM 2** | M1 ack (7T) + push PCH (3T) + push PCL (3T) + vector-lo read (3T) + vector-hi read (3T) | **19** | +1 | 0 | 0 | [(I<<8)|vec] | vector |

**Notes pinned (the implementer must honor these exactly — they are the UAT assertions):**

- **NMI saves IFF1 into IFF2 and clears IFF1; IFF2 is the saved copy** that a later `RETN` restores. Standard
  Z80: NMI does `IFF2 := IFF1; IFF1 := 0`. (Some references say NMI leaves IFF2 unchanged — that is WRONG for
  the documented Zilog model; NMI copies IFF1→IFF2 so RETN can restore the pre-NMI enable state. Pin the
  copy-then-clear and assert the RETN round-trip in the UAT.)
- **A maskable IRQ acknowledge clears BOTH IFF1 and IFF2** (so a nested IRQ is masked until `EI`). NMI does
  NOT clear IFF2 (it saves into it).
- **R increments by exactly 1** on every serviced interrupt (NMI and maskable alike) — the acknowledge is one
  M1 cycle. Preserve bit 7 (`R & 0x80`).
- **WZ (MEMPTR) on service:** WZ takes the new PC (the vector). For IM 2, WZ = the fetched 16-bit vector; for
  IM 0/1 and NMI, WZ = the fixed/RST vector. (There is no TomHarte oracle for servicing WZ; this follows the
  documented MEMPTR rule that a CALL-like flow sets WZ to the destination, consistent with `EmitZ80EdRetn`'s
  `WZ = popped PC` and the flow-op WZ rules in `EmitZ80FlowBody`. Pin it and note it is rule-derived, not
  vector-derived, in the closeout.)
- **The IM 0 common case is RST n.** The device classically supplies an `RST n` opcode (0xC7|0xCF|…|0xFF →
  vectors 0x00, 0x08, …, 0x38). M3.5-1 models the common form: default `PC := 0x0038` (RST 38h, the most
  common IM 0 / power-on case), with a hook (`InterruptData` → decode the RST vector if a device byte is
  configured: `vector = InterruptData & 0x38` when `InterruptData` is an `RST` opcode `(InterruptData & 0xC7)
  == 0xC7`, else `0x0038`). Charge 13 T (the RST-n service cost). Document: full IM 0 arbitrary-opcode
  execution is out of scope (it is not vector-testable and is a rare configuration).

---

## File Structure

| File | Create/Modify | Responsibility |
|---|---|---|
| `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` | Modify | The whole interrupt policy: `_irqLine`/`_nmiLine`/`_nmiPending`/`_eiPending` fields; `InterruptData`; `SetIrqLine`/`SetNmiLine`; `InterruptPending`; `TryServiceInterrupt()` (NMI + IM 0/1/2); `OnInterruptEnable()` (the EI-delay hook); the boundary EI-delay commit; the HALT wake (clear `_halted`); R bump on ack. |
| `src/CpuEmulator.Generators/CpuEmitter.cs` | Modify | The ONE generator touch: route the `Ei` micro-op body through the partial hook `OnInterruptEnable()` (structured-CPU only), so the EI-delay latch lives in the partial. The `Di` body stays immediate. |
| `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` | Create | The dedicated interrupt UAT (D5): IM0/IM1/IM2/NMI + EI-delay + DI-mask + HALT-wake + the RETN IFF restore round-trip + R/WZ/cycle-cost assertions. |
| `tests/CpuEmulator.Tests/Generators/Z80EiDelayTests.cs` | Create | A synthetic-spec test proving the generator routes `Ei` through `OnInterruptEnable()` (the partial hook is called, not an immediate `_iff1=true`), and that a CPU WITHOUT the hook (the 6502 path / a synthetic spec that does not declare it) is unaffected. |

> **No `Z80Spec.cs` regen and no dataset change.** The interrupt policy is hand-written in the partial; the
> `EI`/`DI` rows already exist. The only generated-output change is the `Ei` body in `Z80Cpu.g.cs` (emitted
> at build time, not committed) — `Z80Spec.cs` (the `Insn` table) is byte-identical, and the 6502 `.g.cs` is
> byte-identical (the hook is structured-only). Run `RegeneratedSpecTests` to confirm.

---

## TDD tasks

> Each task: failing test(s) first, then implement to green, then a full-suite gate (incl. the 6502
> additivity guard `RegeneratedSpecTests` + the WHOLE Z80 TomHarte sweep staying green at the universal
> Q/WZ/IM bar), then commit. Tasks are dependency-ordered so the suite builds and stays green after every
> task. Literal code is given for every load-bearing piece. The interrupt UAT (Tasks 2–8) constructs the
> `Z80Cpu` directly (no live vector); the EI-delay generator hook (Task 6) is proven synthetically first.

---

### Task 0: Baseline + shipped-surface recon (NO code change)

**Files:** none (read-only).

- [ ] **Step 1: Branch.** This is implementation work — create the branch off `main` (HEAD `af491eb`, M3.4e
  merged):
  Run: `git checkout main && git pull && git checkout -b feat/m3-z80-interrupt-servicing`
  Expected: a fresh branch off the M3.4e merge.

- [ ] **Step 2: Confirm the green baseline.**
  Run: `dotnet test`
  Expected: 0 failures, 0 unexpected skips. Record the EXACT test count (the closeout pins it — the overview
  notes ≈3424/0/0 for the full Z80 ISA).
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean (no warnings).

- [ ] **Step 3: Recon — read (do NOT edit) and confirm each cited surface holds (the "Ground truth" +
  "RECON FINDINGS" sections are the checklist):**
  - `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs:23,27-35,47,61-82,85-117,124,130-134` (the `_halted`/`_iff1`/`_iff2`
    latches; `Im`; `Reset`; the bus helpers; the `TryServiceInterrupt`/`InterruptPending`/`SetIrqLine`/
    `SetNmiLine` stubs; the R-refresh).
  - `src/CpuEmulator.Cpus.Mos6502/Mos6502Cpu.cs:22-24,54-107` (the 6502 interrupt precedent — the field set,
    the edge/level line setters, `InterruptPending`, the `TryServiceInterrupt` push sequence).
  - `src/CpuEmulator.Generators/CpuEmitter.cs:145-200` (the generated `Step`: `TryServiceInterrupt()` first,
    then the `Halted` idle, then the structured fetch + `OnInstructionFetched`), `:250-258` (`Run`),
    `:2017-2022` (the `Di`/`Ei` micro-op bodies — confirm `Ei` sets `_iff1=true; _iff2=true` immediately),
    `:3023-3035` (`EmitZ80EdRetn` — `_iff1 = _iff2`).
  - `tests/CpuEmulator.Tests/TomHarte/Z80TomHarteRunnerSelfTests.cs` (the inline-fixture UAT pattern),
    `Z80TomHarteRunner.cs:26-54` (the `Z80Cpu` construction + state-set shape the UAT mirrors).

- [ ] **Step 4: Re-derive the timing/IFF/R/WZ rules.** Confirm the four-row table above against the Zilog
  UM008 §6 + the community timing tables (NMI=11, IM0=13, IM1=13, IM2=19; R+1; NMI saves IFF1→IFF2 + clears
  IFF1; maskable ack clears both IFF). Note any source disagreement in the closeout and pin the documented
  Zilog model. (No vector oracle exists for servicing — these are manual-derived, the honest deviation.)

- [ ] **Step 5:** No commit (read-only). Proceed to Task 1.

---

### Task 1: The interrupt line setters + `InterruptPending` (TDD)

> Add the line-state fields + the edge/level setters + the `InterruptPending` predicate. No servicing yet
> (`TryServiceInterrupt` still returns false) — this task makes the lines observable so the UAT can assert
> `InterruptPending` flips correctly under IFF/line state.

**Files:**
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (fields + setters + `InterruptPending`)
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (create — the line/pending portion)

- [ ] **Step 1: Write the failing test.** Create
  `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs`:

```csharp
using CpuEmulator.Core;
using CpuEmulator.Cpus.Z80;
using CpuEmulator.Tests.Mos6502;   // TracingAddressSpace
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M3.5-1 — the dedicated, deterministic interrupt-servicing UAT (decision D5). Interrupt SERVICING is
/// NOT single-step-vector-testable (the SingleStepTests vectors cover instruction execution, not the
/// CPU's response to an asserted IRQ/NMI line), so this UAT hand-constructs each case: set IM mode + IFF
/// state + memory + a pending line, Step() (which services), and assert the serviced vector PC, the
/// pushed return address, IFF1/IFF2 after, the cycle cost, R, WZ, and the HALT wake. ZEXALL (M3.5-2) is
/// the integration confirmation; this UAT is the primary gate.
/// </summary>
public class Z80InterruptServicingTests
{
    /// <summary>Build a Z80 with 64KiB program RAM + a 16-bit I/O space (both tracing), like the TomHarte
    /// runner — but constructed directly (no vector case). Returns the CPU + the inner program space so a
    /// test can seed/read memory.</summary>
    private static (Z80Cpu Cpu, AddressSpace Mem) BuildCpu()
    {
        var inner = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        inner.MapMemory(0x0000, new byte[0x10000], writable: true);
        var bus = new TracingAddressSpace(inner);
        var ioInner = new AddressSpace(AddressSpaceKind.Io, addressBits: 16);
        ioInner.MapMemory(0x0000, new byte[0x10000], writable: true);
        var io = new TracingAddressSpace(ioInner);
        var cpu = new Z80Cpu(bus, io);
        return (cpu, inner);
    }

    [Fact]
    public void InterruptPending_is_gated_by_IFF1_for_maskable_IRQ()
    {
        var (cpu, _) = BuildCpu();
        cpu.Iff1 = false;
        cpu.SetIrqLine(true);
        Assert.False(cpu.InterruptPending);   // IRQ asserted but IFF1 clear → masked
        cpu.Iff1 = true;
        Assert.True(cpu.InterruptPending);     // IRQ asserted + IFF1 set → pending
        cpu.SetIrqLine(false);
        Assert.False(cpu.InterruptPending);
    }

    [Fact]
    public void InterruptPending_is_set_by_NMI_regardless_of_IFF1()
    {
        var (cpu, _) = BuildCpu();
        cpu.Iff1 = false;             // NMI is non-maskable
        cpu.SetNmiLine(true);          // rising edge latches
        Assert.True(cpu.InterruptPending);
    }

    [Fact]
    public void SetNmiLine_is_edge_triggered()
    {
        var (cpu, _) = BuildCpu();
        cpu.SetNmiLine(true);          // rising edge → pending
        Assert.True(cpu.InterruptPending);
        cpu.SetNmiLine(true);          // held high, no new edge — still pending (not double-latched)
        Assert.True(cpu.InterruptPending);
        cpu.SetNmiLine(false);         // falling edge does NOT clear the pending latch
        Assert.True(cpu.InterruptPending);
    }
}
```

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80InterruptServicingTests"`
  Expected: FAIL — `InterruptPending` is `=> false`; `SetIrqLine`/`SetNmiLine` are no-ops.

- [ ] **Step 3: Add the fields + setters + predicate.** In `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs`, replace the
  stub setters (`:74-75`) and `InterruptPending` (`:78`). First add the fields after `_iff2` (`:28`):

```csharp
    /// <summary>The maskable INT line LEVEL (M3.5-1). Level-sensitive: serviced at any instruction
    /// boundary while high AND IFF1 is set. Set by the host/peripheral via SetIrqLine.</summary>
    private bool _irqLine;

    /// <summary>The NMI line level (for edge detection) + the edge-latched pending flag (M3.5-1). NMI is
    /// edge-triggered: a rising edge sets _nmiPending; the latch clears when serviced and on Reset.</summary>
    private bool _nmiLine;
    private bool _nmiPending;
```

  Then replace the two setter stubs and `InterruptPending`:

```csharp
    /// <summary>The maskable INT line is level-sensitive: sampled at every instruction boundary and
    /// serviced when high and IFF1 is set (M3.5-1).</summary>
    public void SetIrqLine(bool asserted) => _irqLine = asserted;

    /// <summary>NMI is edge-triggered: a rising edge sets the pending latch; the latch clears when
    /// serviced and on Reset. A held-high line never re-fires until released and re-asserted (M3.5-1).</summary>
    public void SetNmiLine(bool asserted)
    {
        if (asserted && !_nmiLine)
            _nmiPending = true;
        _nmiLine = asserted;
    }

    /// <summary>True exactly when the next Step will service an interrupt — NMI (non-maskable, edge-
    /// latched) or a maskable INT gated by IFF1 (M3.5-1). The JIT boundary-samples this policy-blind.</summary>
    public partial bool InterruptPending => _nmiPending || (_irqLine && _iff1);
```

- [ ] **Step 4: Clear `_nmiPending` in `Reset`.** In `Reset()` (`:61-70`), after `_halted = false;` add:

```csharp
        _nmiPending = false;
        _nmiLine = false;
        _irqLine = false;
```

- [ ] **Step 5: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80InterruptServicingTests"`
  Expected: PASS (3 facts).

- [ ] **Step 6: Full gate.**
  Run: `dotnet test` → all green (the whole Z80 TomHarte sweep is unaffected — `TryServiceInterrupt` still
  returns false, so no case services).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — no
  generator change yet).

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Cpus.Z80/Z80Cpu.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): interrupt line setters + IFF1-gated InterruptPending (no servicing yet)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 2: `TryServiceInterrupt` — IM 1 (the simplest maskable form: RST 38h) (TDD)

> Implement the maskable-service skeleton for IM 1 first (the simplest: a fixed vector, no device byte, no
> table read). This establishes the push sequence, the IFF clear, the R bump, the WZ write, the cycle
> charge, and the structure the other modes extend. NMI + IM 0 + IM 2 are added in Tasks 3–5.

**Files:**
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (`TryServiceInterrupt` + a `PushPc` + `BumpRefresh` helper)
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (extend — the IM 1 case)

- [ ] **Step 1: Write the failing test.** Add to `Z80InterruptServicingTests.cs`:

```csharp
    [Fact]
    public void IM1_services_to_0x0038_pushing_PC_clearing_IFF_bumping_R()
    {
        var (cpu, mem) = BuildCpu();
        // Place a NOP at 0x0038 (the IM1 handler) — not executed here; we only assert the service vector.
        mem.Write8(0x0038, 0x00);
        cpu.SetRegister("PC", 0x1234);   // the instruction that WOULD run next → pushed return address
        cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("R", 0x10);
        cpu.SetRegister("WZ", 0x0000);
        cpu.Im = 1;
        cpu.Iff1 = true; cpu.Iff2 = true;
        cpu.SetIrqLine(true);

        long before = cpu.CycleCount;
        cpu.Step();   // services the interrupt (does NOT fetch an opcode)

        Assert.Equal(0x0038u, (uint)cpu.GetRegister("PC"));   // IM1 → RST 38h
        Assert.Equal(0xFFEEu, (uint)cpu.GetRegister("SP"));   // SP -= 2 (two pushes)
        Assert.Equal(0x34, mem.Read8(0xFFEE));                // PCL pushed
        Assert.Equal(0x12, mem.Read8(0xFFEF));                // PCH pushed
        Assert.Equal(0x0038u, (uint)cpu.GetRegister("WZ"));   // WZ = vector
        Assert.False(cpu.Iff1);                                // maskable ack clears IFF1
        Assert.False(cpu.Iff2);                                // ...and IFF2
        Assert.Equal(0x11u, (uint)cpu.GetRegister("R"));      // R low-7 bumped by 1 (0x10 → 0x11)
        Assert.Equal(13L, cpu.CycleCount - before);           // IM1 = 13 T-states
    }
```

  > **Push order/addresses (re-confirm against the 6502 precedent `Mos6502Cpu.cs:95-98` + the Z80 little-
  > endian stack):** the Z80 pushes PCH first (to `SP-1`), then PCL (to `SP-2`), leaving SP = SP-2. So PCL
  > lands at the lower address (`0xFFEE`) and PCH at `0xFFEF` — assert exactly that.

- [ ] **Step 2: Run the test to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~IM1_services"`
  Expected: FAIL — `TryServiceInterrupt` returns false; nothing happens; PC stays 0x1234.

- [ ] **Step 3: Implement `TryServiceInterrupt` (IM 1 arm) + helpers.** In `Z80Cpu.cs`, replace the stub
  `private partial bool TryServiceInterrupt() => false;` (`:124`) with:

```csharp
    /// <summary>Instruction-boundary interrupt service (the generated Step calls this before the opcode
    /// fetch — CpuEmitter.cs:147). NMI beats a maskable INT. Returns false when nothing is pending (so
    /// every TomHarte single-step case is unaffected). When it services, it performs the full push/vector
    /// bus sequence itself (charging cycles via ReadBus/WriteBus + the M1-acknowledge internal T-states
    /// via _cycles), saves/clears IFF per the Z80 model, bumps R for the acknowledge M1 cycle, clears the
    /// halted latch (the HALT wake), sets WZ to the vector, and returns true. M3.5-1.</summary>
    private partial bool TryServiceInterrupt()
    {
        // NMI is non-maskable and highest priority; a maskable INT requires IFF1.
        if (!_nmiPending && !(_irqLine && _iff1))
            return false;

        _halted = false;        // the HALT wake — resume fetch on the next Step
        BumpRefresh();          // the acknowledge is one M1 cycle → R low-7 += 1

        if (_nmiPending)
        {
            _nmiPending = false;
            _iff2 = _iff1;      // NMI saves IFF1 into IFF2 (RETN restores it)
            _iff1 = false;      // ...and disables maskable interrupts
            PushPc();
            PC = 0x0066;
            WZ = 0x0066;
            _cycles += 11 - 6;  // NMI = 11 T; PushPc charged 6 (two 3-T writes)
            return true;
        }

        // Maskable INT acknowledge: clear BOTH flip-flops (nested IRQ masked until EI).
        _iff1 = false;
        _iff2 = false;

        switch (Im)
        {
            // IM 0 (Task 4) and IM 2 (Task 5) are added below; IM 1 first:
            default: // IM 1 (and the IM-1 fallback): fixed RST 38h.
                PushPc();
                PC = 0x0038;
                WZ = 0x0038;
                _cycles += 13 - 6;  // IM1 = 13 T; PushPc charged 6
                return true;
        }
    }

    /// <summary>Push the current PC (PCH then PCL, little-endian on the descending stack), charging the
    /// two write cycles. The pushed PC is the return address — the instruction that would have run next.</summary>
    private void PushPc()
    {
        SP = unchecked((ushort)(SP - 1));
        WriteBus(SP, unchecked((byte)(PC >> 8)));   // PCH
        SP = unchecked((ushort)(SP - 1));
        WriteBus(SP, unchecked((byte)PC));          // PCL
    }

    /// <summary>Bump R's low 7 bits (bit 7 preserved) — the interrupt acknowledge is an M1 cycle, so R
    /// increments exactly as a one-byte opcode fetch does (same formula as OnInstructionFetched).</summary>
    private void BumpRefresh() => R = (byte)((R & 0x80) | ((R + 1) & 0x7F));
```

  > **Cycle bookkeeping:** `PushPc` does two `WriteBus` calls = +2 cycles (one each). But each Z80 stack
  > write is a 3-T machine cycle, so the two writes are 6 T total, of which `WriteBus` charged 2 — the
  > remaining 4 are folded into the `_cycles += <total> - 6` lump (the `- 6` accounts for the two writes'
  > full 3-T cost, and the `WriteBus`'s 2 cycles are ALREADY counted, so the net add is `<total> - 6` PLUS
  > the 2 from WriteBus = `<total> - 4`). **CONFIRM the arithmetic against the UAT's exact T-count
  > assertion (13 for IM1) and adjust the `- 6` constant if the count is off by the WriteBus-charged 2.**
  > The UAT's `Assert.Equal(13L, ...)` is the oracle — make the constant match. (The cleanest form:
  > `_cycles += 13 - 2;` if `PushPc` already charged 2 via the two WriteBus calls and the M1 ack + internal
  > is `13 - 2 writes-charged - ... ` — derive empirically: run the test, read the actual count, set the
  > constant so it equals 13.)

  > **IMPORTANT — make the cycle constant match the assertion, do not guess.** `ReadBus`/`WriteBus` each
  > charge exactly 1 cycle. `PushPc` charges 2 (two writes). So for IM1 the explicit add must be `13 - 2 =
  > 11` to reach 13 total (`_cycles += 11;`). Use `_cycles += 13 - 2;` (and analogously NMI `11 - 2`, IM0
  > `13 - 2`, IM2 `19 - 4` since IM2 also does 2 vector reads). Re-derive each against its UAT assertion.

- [ ] **Step 4: Fix the cycle constants to match the bus-charge model.** Replace the `_cycles += <T> - 6;`
  lines with the WriteBus-aware form (each WriteBus/ReadBus already charged 1):
  - NMI: `_cycles += 11 - 2;` (PushPc charged 2).
  - IM 1: `_cycles += 13 - 2;` (PushPc charged 2).
  (IM 0 and IM 2 constants land in Tasks 4–5.)

- [ ] **Step 5: Run the test to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~IM1_services"`
  Expected: PASS. If the cycle count is off, adjust the `_cycles += 13 - 2;` constant so `CycleCount - before
  == 13` (the assertion is the oracle).

- [ ] **Step 6: Full gate.**
  Run: `dotnet test` → all green. **CRITICAL:** the whole Z80 TomHarte sweep (base→FDCB) must stay green —
  `TryServiceInterrupt` returns false on every single-step case (no line asserted, IFF irrelevant), so no
  case services. Confirm with:
  Run: `dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"` → 0 failures.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — no
  generator change).

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Cpus.Z80/Z80Cpu.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): TryServiceInterrupt IM1 (RST 38h) — push PC, clear IFF, bump R, WZ, 13T

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1.

---

### Task 3: `TryServiceInterrupt` — NMI (0x0066, IFF save/clear) + the RETN restore round-trip (TDD)

> NMI is already implemented in the Task-2 skeleton; this task ADDS the UAT cases that pin the NMI vector,
> the IFF1→IFF2 save + IFF1 clear, the 11-T cost, and — the load-bearing round-trip — that a `RETN` after
> an NMI restores IFF1 from the saved IFF2.

**Files:**
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (only if the NMI arm needs a fix found by the new test)
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (extend — NMI + RETN round-trip)

- [ ] **Step 1: Write the failing tests.** Add to `Z80InterruptServicingTests.cs`:

```csharp
    [Fact]
    public void NMI_services_to_0x0066_saving_IFF1_into_IFF2_and_clearing_IFF1()
    {
        var (cpu, mem) = BuildCpu();
        cpu.SetRegister("PC", 0x4000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("R", 0x00);
        cpu.Iff1 = true; cpu.Iff2 = true;     // both enabled before NMI
        cpu.SetNmiLine(true);                  // edge → pending (non-maskable)

        long before = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(0x0066u, (uint)cpu.GetRegister("PC"));   // NMI vector
        Assert.Equal(0xFFEEu, (uint)cpu.GetRegister("SP"));
        Assert.Equal(0x00, mem.Read8(0xFFEE));                // PCL
        Assert.Equal(0x40, mem.Read8(0xFFEF));                // PCH
        Assert.False(cpu.Iff1);                                // IFF1 cleared
        Assert.True(cpu.Iff2);                                 // IFF2 = saved old IFF1 (was true)
        Assert.Equal(0x0066u, (uint)cpu.GetRegister("WZ"));
        Assert.Equal(0x01u, (uint)cpu.GetRegister("R"));      // R bumped by 1
        Assert.Equal(11L, cpu.CycleCount - before);           // NMI = 11 T-states
    }

    [Fact]
    public void NMI_then_RETN_restores_IFF1_from_saved_IFF2()
    {
        var (cpu, mem) = BuildCpu();
        // Handler at 0x0066: RETN (ED 45).
        mem.Write8(0x0066, 0xED); mem.Write8(0x0067, 0x45);
        cpu.SetRegister("PC", 0x4000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.Iff1 = true; cpu.Iff2 = true;
        cpu.SetNmiLine(true);

        cpu.Step();                 // service NMI → IFF1=0, IFF2=1, PC=0x0066
        Assert.False(cpu.Iff1);
        cpu.Step();                 // execute RETN at 0x0066 → IFF1 = IFF2 = 1, PC = 0x4000 (popped)
        Assert.True(cpu.Iff1);       // restored from the saved IFF2
        Assert.Equal(0x4000u, (uint)cpu.GetRegister("PC"));
    }
```

- [ ] **Step 2: Run to verify.**
  Run: `dotnet test --filter "FullyQualifiedName~NMI_"`
  Expected: PASS (the NMI arm from Task 2 already implements this). If the IFF save semantics or cycle count
  are wrong, FIX the NMI arm in `TryServiceInterrupt` (the IFF2 save: `_iff2 = _iff1;` BEFORE `_iff1 =
  false;`) and the `_cycles += 11 - 2;` constant, then re-run.

  > **If the round-trip test fails on the RETN step:** confirm `EmitZ80EdRetn` (`CpuEmitter.cs:3033`) emits
  > `_iff1 = _iff2;`. It does — so after NMI sets `_iff2 = oldIff1 (true)`, RETN restores `_iff1 = true`.
  > If it fails, the bug is the NMI arm's IFF save ORDER (must save before clear).

- [ ] **Step 3: Full gate.**
  Run: `dotnet test` → all green (Z80 TomHarte sweep unaffected).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 4: Commit.**

```bash
git add src/CpuEmulator.Cpus.Z80/Z80Cpu.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): NMI servicing (0x0066, IFF1->IFF2 save) + the RETN IFF-restore round-trip UAT

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 4: `TryServiceInterrupt` — IM 0 (the common RST-n form + the device byte) (TDD)

> IM 0: the device supplies an opcode (classically `RST n`). Model the common RST-n form, defaulting to RST
> 38h, with `InterruptData` decoding the RST vector when a device byte is configured. Charge 13 T (the RST-n
> service cost, structurally identical to IM 1).

**Files:**
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (`InterruptData` field + the IM 0 switch arm)
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (extend — IM 0 default + device byte)

- [ ] **Step 1: Add the `InterruptData` field.** In `Z80Cpu.cs`, after `_nmiPending`, add:

```csharp
    /// <summary>The byte the device places on the data bus during an interrupt acknowledge (M3.5-1).
    /// IM 0 decodes it as the supplied opcode (the common RST n case); IM 2 uses it as the low byte of the
    /// vector-table pointer. Host/UAT-settable; default 0xFF (IM 0 → RST 38h, the common power-on form).</summary>
    public byte InterruptData { get; set; } = 0xFF;
```

- [ ] **Step 2: Write the failing tests.** Add to `Z80InterruptServicingTests.cs`:

```csharp
    [Fact]
    public void IM0_defaults_to_RST_38h()
    {
        var (cpu, mem) = BuildCpu();
        cpu.SetRegister("PC", 0x2000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 0;
        cpu.Iff1 = true; cpu.Iff2 = true;
        cpu.SetIrqLine(true);
        // InterruptData defaults to 0xFF (RST 38h opcode) → vector 0x0038.

        long before = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(0x0038u, (uint)cpu.GetRegister("PC"));
        Assert.Equal(0x00, mem.Read8(0xFFEE));                // PCL
        Assert.Equal(0x20, mem.Read8(0xFFEF));                // PCH
        Assert.False(cpu.Iff1); Assert.False(cpu.Iff2);
        Assert.Equal(13L, cpu.CycleCount - before);           // IM0 RST = 13 T
    }

    [Fact]
    public void IM0_decodes_the_device_RST_opcode()
    {
        var (cpu, _) = BuildCpu();
        cpu.SetRegister("PC", 0x2000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 0;
        cpu.Iff1 = true;
        cpu.InterruptData = 0xDF;   // RST 18h opcode (0xDF) → vector 0x0018
        cpu.SetIrqLine(true);

        cpu.Step();
        Assert.Equal(0x0018u, (uint)cpu.GetRegister("PC"));
    }
```

- [ ] **Step 3: Run to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~IM0_"`
  Expected: FAIL — `Im == 0` falls through the `default` (IM 1) arm → PC = 0x0038 by coincidence for the
  default case, but the device-byte case (RST 18h → 0x0018) FAILS (it would give 0x0038). Both must route
  through the IM 0 arm.

- [ ] **Step 4: Add the IM 0 arm to the `switch (Im)`.** In `TryServiceInterrupt`, before the `default`
  (IM 1) arm, add `case 0:`:

```csharp
            case 0: // IM 0: the device supplies an opcode — model the common RST n form.
            {
                // An RST opcode is 11_yyy_111 (0xC7|y<<3); its vector is (y<<3) = opcode & 0x38.
                // Default (InterruptData 0xFF = RST 38h) → 0x0038. Any non-RST byte → 0x0038 fallback.
                ushort vector = (InterruptData & 0xC7) == 0xC7
                    ? (ushort)(InterruptData & 0x38)
                    : (ushort)0x0038;
                PushPc();
                PC = vector;
                WZ = vector;
                _cycles += 13 - 2;  // IM0 RST = 13 T; PushPc charged 2
                return true;
            }
```

  > Keep the `default:` arm as IM 1 (it also handles any unexpected `Im` value as the safe RST-38h fallback).
  > Add an explicit `case 1:` mirroring `default` if the reviewer prefers explicitness; functionally equal.

- [ ] **Step 5: Run to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~IM0_"`
  Expected: PASS (both facts). Adjust the cycle constant if the count is off (the assertion is the oracle).

- [ ] **Step 6: Full gate.**
  Run: `dotnet test` → all green (Z80 TomHarte sweep unaffected).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 7: Commit.**

```bash
git add src/CpuEmulator.Cpus.Z80/Z80Cpu.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): IM0 servicing (common RST-n form + device-byte RST decode), 13T

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 5: `TryServiceInterrupt` — IM 2 (the I-register vector table) (TDD)

> IM 2: form `addr = (I << 8) | (InterruptData & 0xFE)`, read the 16-bit vector from `addr` (lo then hi),
> jump to it. WZ = the fetched vector. Charge 19 T.

**Files:**
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (the IM 2 switch arm)
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (extend — IM 2)

- [ ] **Step 1: Write the failing test.** Add to `Z80InterruptServicingTests.cs`:

```csharp
    [Fact]
    public void IM2_reads_the_vector_from_the_I_register_table()
    {
        var (cpu, mem) = BuildCpu();
        // I = 0x12, device byte = 0x80 → table pointer 0x1280; vector stored there = 0x9ABC.
        mem.Write8(0x1280, 0xBC);   // vector lo
        mem.Write8(0x1281, 0x9A);   // vector hi
        cpu.SetRegister("PC", 0x3000);
        cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("I", 0x12);
        cpu.Im = 2;
        cpu.Iff1 = true;
        cpu.InterruptData = 0x80;
        cpu.SetIrqLine(true);

        long before = cpu.CycleCount;
        cpu.Step();

        Assert.Equal(0x9ABCu, (uint)cpu.GetRegister("PC"));   // vector from the table
        Assert.Equal(0x9ABCu, (uint)cpu.GetRegister("WZ"));
        Assert.Equal(0x00, mem.Read8(0xFFEE));                // PCL of the return address (0x3000)
        Assert.Equal(0x30, mem.Read8(0xFFEF));                // PCH
        Assert.False(cpu.Iff1); Assert.False(cpu.Iff2);
        Assert.Equal(19L, cpu.CycleCount - before);           // IM2 = 19 T-states
    }

    [Fact]
    public void IM2_masks_the_device_byte_low_bit()
    {
        var (cpu, mem) = BuildCpu();
        // Device byte 0x81 → masked to 0x80 (the table is word-aligned: bit 0 cleared).
        mem.Write8(0x1280, 0x11); mem.Write8(0x1281, 0x22);   // vector 0x2211 at 0x1280
        cpu.SetRegister("PC", 0x3000); cpu.SetRegister("SP", 0xFFF0);
        cpu.SetRegister("I", 0x12); cpu.Im = 2; cpu.Iff1 = true;
        cpu.InterruptData = 0x81;   // low bit set → masked off
        cpu.SetIrqLine(true);
        cpu.Step();
        Assert.Equal(0x2211u, (uint)cpu.GetRegister("PC"));   // read from 0x1280, not 0x1281
    }
```

- [ ] **Step 2: Run to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~IM2_"`
  Expected: FAIL — `Im == 2` falls through to the IM-1 `default` → PC = 0x0038, not the table vector.

- [ ] **Step 3: Add the IM 2 arm.** In the `switch (Im)`, before `default`, add `case 2:`:

```csharp
            case 2: // IM 2: I-register high byte + device-byte low byte → table pointer → vector.
            {
                ushort ptr = unchecked((ushort)((I << 8) | (InterruptData & 0xFE)));
                byte vlo = ReadBus(ptr);
                byte vhi = ReadBus(unchecked((ushort)(ptr + 1)));
                ushort vector = unchecked((ushort)(vlo | (vhi << 8)));
                PushPc();
                PC = vector;
                WZ = vector;
                _cycles += 19 - 4;  // IM2 = 19 T; PushPc(2) + two vector ReadBus(2) charged 4
                return true;
            }
```

- [ ] **Step 4: Run to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~IM2_"`
  Expected: PASS (both facts). Adjust the cycle constant to hit 19 if off.

- [ ] **Step 5: Full gate.**
  Run: `dotnet test` → all green (Z80 TomHarte sweep unaffected).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 6: Commit.**

```bash
git add src/CpuEmulator.Cpus.Z80/Z80Cpu.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): IM2 servicing (I-register vector table, device-byte index), 19T

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 6: The EI one-instruction delay — the partial hook (the ONE generator touch) (TDD)

> `EI` enables interrupts only AFTER the instruction following it. Today the `Ei` op body sets
> `_iff1/_iff2` immediately (`CpuEmitter.cs:2020-2022`). M3.5-1 routes `Ei` through a partial hook
> `OnInterruptEnable()` so the partial holds an `_eiPending` delay latch and commits the enable one
> instruction later — and crucially defers SERVICING until after that following instruction. Proven
> synthetically first (the generator routes `Ei` through the hook), then in the interrupt UAT (an IRQ does
> NOT fire on the instruction immediately after EI).

**Files:**
- Modify: `src/CpuEmulator.Generators/CpuEmitter.cs:2017-2022` (the `Ei` micro-op body → the partial hook)
- Modify: `src/CpuEmulator.Cpus.Z80/Z80Cpu.cs` (the `_eiPending` latch + `OnInterruptEnable()` + the
  boundary commit in `TryServiceInterrupt`)
- Test: `tests/CpuEmulator.Tests/Generators/Z80EiDelayTests.cs` (create — the synthetic generator-routing proof)
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (extend — the EI-delay behavior)

#### 6a — the generator routes `Ei` through `OnInterruptEnable()` (structured CPUs only)

- [ ] **Step 1: Write the failing synthetic test.** Create
  `tests/CpuEmulator.Tests/Generators/Z80EiDelayTests.cs`. It compiles a synthetic structured Z80-like CPU
  with an `EI` op (0xFB) and a partial that records whether `OnInterruptEnable()` was called, asserting the
  generated `Ei` body calls the hook rather than writing `_iff1` directly:

```csharp
using Xunit;

namespace CpuEmulator.Tests.Generators;

public class Z80EiDelayTests
{
    private const string Source = """
        using CpuEmulator.Core;
        using CpuEmulator.Core.Specification;
        using static CpuEmulator.Core.Specification.Spec;

        namespace Demo;

        [CpuSpecification("eid")]
        public static class EidSpec
        {
            public static readonly RegisterDef[] Registers =
            [
                new("A", 8), new("F", 8, RegisterRole.Status),
                new("SP", 16, RegisterRole.StackPointer),
                new("PC", 16, RegisterRole.ProgramCounter),
                new("WZ", 16),
            ];
            public static readonly FlagLayout Flags = new([
                new("S", 7), new("Z", 6), new("Y", 5), new("H", 4),
                new("X", 3), new("P", 2), new("N", 1), new("C", 0)]);
            public static readonly DecodeStructure Decode = new(
                Prefixes: [], ModRmOpcodes: [], SubFieldOpcodes: []);
            public static readonly InstructionDef[] Instructions =
            [
                Insn(0xFB, "EI", AddrMode.Implied, [Ei()]),
            ];
        }

        public sealed partial class EidCpu
        {
            private readonly byte[] _mem;
            public byte Q;
            public bool _iff1, _iff2;
            public bool HookCalled;
            public EidCpu(byte[] mem) { _mem = mem; }
            public void Reset() { }
            public void SetIrqLine(bool a) { }
            public void SetNmiLine(bool a) { }
            public partial bool InterruptPending => false;
            private partial bool TryServiceInterrupt() => false;
            partial void OnInstructionFetched(int keyBytes) { }
            partial void OnInterruptEnable() { HookCalled = true; }
            private byte ReadBus(uint a) => _mem[a & 0xFFFF];
            private void WriteBus(uint a, byte v) => _mem[a & 0xFFFF] = v;
            private void HandleUndefinedOpcode(byte op) { }
        }
        """;

    [Fact]
    public void EI_body_routes_through_the_OnInterruptEnable_hook()
    {
        var t = GeneratorTestHost.CompileAndLoadType(Source, "Demo.EidCpu");
        var mem = new byte[0x10000];
        mem[0] = 0xFB;   // EI
        var cpu = System.Activator.CreateInstance(t, new object[] { mem })!;
        t.GetMethod("SetRegister")!.Invoke(cpu, new object[] { "PC", 0UL });
        t.GetMethod("Step")!.Invoke(cpu, null);
        // The generated Ei body called OnInterruptEnable() instead of setting _iff1 directly.
        Assert.True((bool)t.GetField("HookCalled")!.GetValue(cpu)!);
    }
}
```

- [ ] **Step 2: Run to verify it fails.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EiDelayTests"`
  Expected: FAIL — the generated `Ei` body sets `_iff1 = true; _iff2 = true;` and never calls the hook;
  `HookCalled` stays false. (It may also fail to COMPILE if `OnInterruptEnable` is declared in the partial
  but the generator does not declare the matching `partial void OnInterruptEnable();` — Step 3 adds it.)

- [ ] **Step 3: Route the `Ei` body through the hook + declare the partial.** In `CpuEmitter.cs`, change the
  `Ei` arm (`:2020-2022`):

```csharp
            case "Di":
                sb.AppendLine("        _iff1 = false; _iff2 = false;");
                break;
            case "Ei":
                // M3.5-1: EI has a one-instruction delay (interrupts enable only AFTER the next
                // instruction). The partial owns the delay latch via OnInterruptEnable(); the generated
                // body delegates to it rather than writing _iff1/_iff2 directly. A structured CPU that
                // does not implement the partial gets a no-op (the delay is a Z80 detail).
                sb.AppendLine("        OnInterruptEnable();");
                break;
```

  And where the other Z80 partial hooks are declared (near `OnInstructionFetched`'s
  `partial void OnInstructionFetched(int keyBytes);` declaration, emitted for structured CPUs at
  `CpuEmitter.cs:208`), add the matching declaration. Find that block and append:

```csharp
            sb.AppendLine();
            sb.AppendLine("    /// <summary>Interrupt-enable hook (M3.5-1): the EI micro-op body calls this");
            sb.AppendLine("    /// instead of writing the IFF latches directly, so the hand-written partial can");
            sb.AppendLine("    /// model the Z80 EI one-instruction delay. Partial — elided when unimplemented");
            sb.AppendLine("    /// (a structured CPU with no EI-delay is unaffected; the 6502 never emits EI).</summary>");
            sb.AppendLine("    partial void OnInterruptEnable();");
```

  > **6502 byte-identity:** the `Ei` arm only fires for a row whose op kind is `Ei` — the 6502 has no such
  > row, so its `EmitZ80Misc` is never reached and its `.g.cs` is byte-identical. The
  > `partial void OnInterruptEnable();` declaration is emitted in the SAME structured-only block as
  > `OnInstructionFetched` (`model.Decode is not null`), so the 6502 (Decode null) never declares it.
  > Confirm the declaration is inside the `if (model.Decode is not null)` block. **Run `RegeneratedSpecTests`
  > — it MUST stay green.**

- [ ] **Step 4: Run to verify it passes.**
  Run: `dotnet test --filter "FullyQualifiedName~Z80EiDelayTests"`
  Expected: PASS.

- [ ] **Step 5: Intermediate gate (before adding the Z80 partial logic).**
  Run: `dotnet build --no-incremental -warnaserror` — **this will FAIL** because the real `Z80Cpu` partial
  does not yet implement `OnInterruptEnable()` and the generated `Z80Cpu.g.cs` now calls it. That is
  expected — proceed to 6b in the SAME task (do not commit a broken build).

#### 6b — the Z80 partial: the `_eiPending` latch + the delayed enable + deferred servicing

- [ ] **Step 6: Write the failing UAT for the EI-delay.** Add to `Z80InterruptServicingTests.cs`:

```csharp
    [Fact]
    public void EI_delays_interrupt_servicing_by_one_instruction()
    {
        var (cpu, mem) = BuildCpu();
        // Program: EI (0xFB) at 0x0000, then NOP (0x00) at 0x0001, then NOP at 0x0002.
        mem.Write8(0x0000, 0xFB); mem.Write8(0x0001, 0x00); mem.Write8(0x0002, 0x00);
        cpu.SetRegister("PC", 0x0000); cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 1;
        cpu.Iff1 = false; cpu.Iff2 = false;   // interrupts start disabled
        cpu.SetIrqLine(true);                  // IRQ asserted the whole time

        cpu.Step();   // EI — sets the delay latch; IFF NOT yet enabled; no service
        Assert.Equal(0x0001u, (uint)cpu.GetRegister("PC"));   // EI ran (PC advanced), no service
        Assert.False(cpu.InterruptPending);                    // IFF1 still false → not pending YET

        cpu.Step();   // NOP at 0x0001 — the EI delay commits IFF1 AFTER this instruction; still no service
                      // (the IRQ is checked at the boundary BEFORE this NOP, when IFF1 was still false).
        Assert.Equal(0x0002u, (uint)cpu.GetRegister("PC"));   // NOP ran, no service
        Assert.True(cpu.Iff1);                                 // now enabled (the delay elapsed)
        Assert.True(cpu.InterruptPending);                     // now pending

        cpu.Step();   // boundary before the next instruction: NOW the IRQ services (IM1 → 0x0038)
        Assert.Equal(0x0038u, (uint)cpu.GetRegister("PC"));
    }

    [Fact]
    public void DI_masks_a_pending_IRQ()
    {
        var (cpu, mem) = BuildCpu();
        mem.Write8(0x0000, 0xF3);   // DI
        cpu.SetRegister("PC", 0x0000); cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 1; cpu.Iff1 = true; cpu.Iff2 = true;
        cpu.SetIrqLine(true);
        cpu.Step();   // DI runs (IFF cleared immediately — no delay on DI); boundary before DI had IFF1
                      // set, but the IRQ services BEFORE the fetch... see the ordering note below.
        // After DI: IFF1 false; a subsequent boundary does not service.
        Assert.False(cpu.Iff1);
        cpu.Step();   // next boundary: IRQ asserted but IFF1 clear → masked; the instruction at PC runs.
        Assert.NotEqual(0x0038u, (uint)cpu.GetRegister("PC"));
    }
```

  > **The ordering subtlety (pin it against the model):** servicing is checked at the boundary BEFORE each
  > fetch. With IFF1 set and an IRQ asserted, the FIRST `Step` would service before running DI. To test the
  > DI mask cleanly, the first test seeds `Iff1 = true` but the intent is "DI clears IFF so later boundaries
  > mask." If the first `Step` services instead of running DI, that is CORRECT Z80 behavior (the IRQ was
  > enabled at that boundary) — adjust the test to start with `Iff1 = false`, run DI (a no-op enable-wise),
  > then assert a later IRQ with IFF1 still false is masked. **Derive the exact expectation from the
  > boundary-before-fetch model and make the assertions match; the model is the oracle, not the prose.**

- [ ] **Step 7: Implement the `_eiPending` latch + the delayed commit in the Z80 partial.** In `Z80Cpu.cs`:
  - Add the latch field after `_nmiPending`:

```csharp
    /// <summary>The EI one-instruction-delay latch (M3.5-1). EI sets it (via OnInterruptEnable); the enable
    /// commits at the NEXT instruction boundary, so an interrupt is not serviced on the instruction
    /// immediately following EI (the documented Z80 quirk). Two-stage: _eiPending counts down one
    /// instruction. Cleared on Reset.</summary>
    private int _eiPending;
```

  - Implement the hook:

```csharp
    /// <summary>The EI micro-op body calls this (M3.5-1). EI enables interrupts only AFTER the following
    /// instruction — so we arm a one-instruction delay rather than setting IFF1/IFF2 immediately. The
    /// commit happens at the next boundary in TryServiceInterrupt's pre-check (CommitEiDelay).</summary>
    partial void OnInterruptEnable() => _eiPending = 2;
```

  > **Why 2:** the latch is decremented once at the boundary AFTER EI's `Step` returns and once more checked
  > before it enables. Concretely: EI's `Step` sets `_eiPending = 2`. The NEXT `Step`'s pre-check decrements
  > to 1 and does NOT yet enable (this is the "instruction after EI"). The `Step` AFTER that decrements to 0
  > and commits `_iff1 = _iff2 = true`. **Re-derive the exact count against the UAT's three-Step sequence;
  > the test pins it. If `= 1` with a different decrement placement yields the correct "service on the
  > boundary after the next instruction," use that — the UAT assertions are the oracle.**

  - Add the commit at the TOP of `TryServiceInterrupt` (before the pending check), so the delay elapses each
    boundary:

```csharp
    private partial bool TryServiceInterrupt()
    {
        // M3.5-1: the EI delay elapses one instruction-boundary at a time. When it reaches the commit
        // point, IFF1/IFF2 turn on — but NOT in time to service at THIS boundary (the enable takes effect
        // for the boundary AFTER the instruction following EI).
        if (_eiPending > 0)
        {
            _eiPending--;
            if (_eiPending == 0)
            {
                _iff1 = true;
                _iff2 = true;
            }
        }

        // ... the existing NMI/maskable pending check + service follows unchanged ...
```

  > **Subtle but load-bearing:** because the commit happens at the START of `TryServiceInterrupt` and the
  > maskable-pending check reads `_iff1` AFTER the commit, the boundary on which IFF1 turns on COULD service
  > immediately — which would be a zero-instruction delay (wrong). The UAT's three-Step sequence is what
  > forces the correct timing. **Implement, run the UAT, and adjust the decrement/commit ORDER until the
  > delay is exactly one instruction** (IRQ asserted throughout: EI's Step → no service; the next Step → no
  > service but IFF1 commits; the Step after → services). The `= 2` count with "decrement-then-commit at the
  > top" gives: Step1(EI, sets 2) → Step2(pre-check: 2→1, no commit; NOP runs) → Step3(pre-check: 1→0,
  > commit IFF1; THEN the pending check sees IFF1 true and services). That is the one-instruction delay.
  > Pin it with the UAT.

  - Clear `_eiPending` in `Reset()`:

```csharp
        _eiPending = 0;
```

- [ ] **Step 8: Regenerate is automatic (the generator runs at build).** No manual regen — the generator
  emits `Z80Cpu.g.cs` at compile time. Build:
  Run: `dotnet build --no-incremental -warnaserror`
  Expected: clean (the partial now implements `OnInterruptEnable()`, so the generated call resolves).

- [ ] **Step 9: Run the EI-delay + DI tests.**
  Run: `dotnet test --filter "FullyQualifiedName~EI_delays OR FullyQualifiedName~DI_masks"`
  Expected: PASS. Adjust the `_eiPending` count/placement against the three-Step sequence until the
  one-instruction delay is exact.

- [ ] **Step 10: CRITICAL — confirm the TomHarte sweep still green (the EI/DI semantics changed).** The
  `EI`/`DI` TomHarte vectors (base-plane 0xFB/0xF3) assert the FINAL `iff1`/`iff2`. The OLD `Ei` body set
  them immediately; the NEW body defers via `_eiPending`. **So a single-step `EI` vector now ends with
  `_iff1` STILL FALSE** (the delay has not elapsed) — which would FAIL the vector if the vector expects
  `iff1 = 1` after EI.
  Run: `dotnet test --filter "FullyQualifiedName~Z80TomHarteTests" 2>&1 | tail -40`
  - **If the EI vector (`fb.json`) fails** because it expects `iff1 = 1` immediately: the TomHarte model
    sets IFF in the final state of the EI instruction itself (the vectors model the architectural latch, not
    the servicing delay). RESOLVE by making `OnInterruptEnable()` ALSO set the architectural IFF immediately
    for the single-step view BUT gate SERVICING separately. **The clean model:** `EI` sets `_iff1 = _iff2 =
    true` immediately (matching the vector's final state) AND arms `_eiPending` as a "do-not-service-yet"
    one-instruction window. `InterruptPending`/`TryServiceInterrupt` then require `_iff1 && _eiPending == 0`.
    Re-derive: read `fb.json` (the EI vector's final `iff1`/`iff2`) at this step and choose the model that
    (a) makes the EI vector green AND (b) makes the EI-delay UAT green. **This is the load-bearing design
    decision of Task 6 — pin it against BOTH oracles.**

  > **Recommended resolution (pin against `fb.json`):** `OnInterruptEnable()` sets `_iff1 = _iff2 = true`
  > immediately (vector-correct) AND sets `_eiPending = 1` (the no-service window). `TryServiceInterrupt`'s
  > maskable check becomes `_irqLine && _iff1 && _eiPending == 0`; `InterruptPending` likewise. Each
  > `TryServiceInterrupt` boundary decrements `_eiPending` toward 0 (so the instruction after EI runs with
  > `_eiPending == 1` → no service, then it decrements to 0 → the next boundary services). Update the UAT's
  > `InterruptPending` expectation accordingly (after EI's Step, `_iff1` is TRUE but `InterruptPending` is
  > FALSE because `_eiPending == 1`). **Re-derive the exact decrement timing against the three-Step UAT and
  > the `fb.json` vector — both must be green.** Rewrite the Task-6 UAT assertions to match this model (the
  > `Assert.False(cpu.Iff1)` lines become `Assert.True(cpu.Iff1)` + `Assert.False(cpu.InterruptPending)`).

- [ ] **Step 11: Full gate.**
  Run: `dotnet test` → all green (the whole suite, incl. the Z80 TomHarte sweep AND the new UATs).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical — the
  `Ei` body + the `partial void OnInterruptEnable()` declaration are structured-only).

- [ ] **Step 12: Commit.**

```bash
git add src/CpuEmulator.Generators/CpuEmitter.cs src/CpuEmulator.Cpus.Z80/Z80Cpu.cs \
        tests/CpuEmulator.Tests/Generators/Z80EiDelayTests.cs \
        tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs
git commit -m "$(cat <<'EOF'
feat(z80): EI one-instruction delay via OnInterruptEnable partial hook (vector + UAT green)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~3.

---

### Task 7: The HALT wake (TDD)

> A HALT'd CPU idles (the generated `Step` idles one cycle on `Halted` after `TryServiceInterrupt`). When an
> interrupt services, `TryServiceInterrupt` clears `_halted` (Task 2 already does `_halted = false;`) and the
> CPU resumes. This task adds the UAT proving HALT-then-IRQ wakes and services, and HALT-then-no-IRQ keeps
> idling (burning cycles, not looping forever).

**Files:**
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (extend — the HALT wake)
- (No source change expected — Task 2's `_halted = false;` is the wake. If the test reveals a gap, fix the
  partial.)

- [ ] **Step 1: Write the failing test.** Add to `Z80InterruptServicingTests.cs`:

```csharp
    [Fact]
    public void HALT_then_IRQ_wakes_and_services()
    {
        var (cpu, mem) = BuildCpu();
        mem.Write8(0x0000, 0x76);   // HALT
        cpu.SetRegister("PC", 0x0000); cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 1; cpu.Iff1 = true; cpu.Iff2 = true;

        cpu.Step();   // execute HALT → _halted set, PC advances past HALT (to 0x0001)
        // While halted with no interrupt, Step idles one cycle and does NOT advance further.
        ulong pcAfterHalt = cpu.GetRegister("PC");
        cpu.Step();   // idle (no interrupt pending) — PC unchanged, one cycle burned
        Assert.Equal(pcAfterHalt, cpu.GetRegister("PC"));

        cpu.SetIrqLine(true);   // now assert IRQ
        cpu.Step();             // services: clears _halted, pushes PC, jumps to 0x0038
        Assert.Equal(0x0038u, (uint)cpu.GetRegister("PC"));
        // The pushed return address is the post-HALT PC (resumes at the instruction after HALT).
        Assert.Equal((byte)(pcAfterHalt & 0xFF), mem.Read8(0xFFEE));
        Assert.Equal((byte)(pcAfterHalt >> 8), mem.Read8(0xFFEF));
    }

    [Fact]
    public void HALT_with_no_interrupt_idles_without_advancing_PC()
    {
        var (cpu, mem) = BuildCpu();
        mem.Write8(0x0000, 0x76);   // HALT
        cpu.SetRegister("PC", 0x0000);
        cpu.Step();   // HALT
        ulong pc = cpu.GetRegister("PC");
        long c0 = cpu.CycleCount;
        for (int i = 0; i < 10; i++) cpu.Step();   // 10 idle steps
        Assert.Equal(pc, cpu.GetRegister("PC"));    // PC frozen while halted
        Assert.True(cpu.CycleCount > c0);           // cycles advanced (idle burns budget, no infinite loop)
    }
```

  > **Pin the post-HALT PC against the generated HALT body.** Confirm whether the Z80's `Halt()` micro-op
  > leaves PC pointing AT the HALT opcode or AFTER it (the Z80 increments PC past HALT, then re-executes the
  > "halt state" internally). Read the `Halt`/`DoHalt` emission and the `76.json` vector's final PC to pin
  > `pcAfterHalt`. The UAT uses whatever `Step` produces (`pcAfterHalt = cpu.GetRegister("PC")`), so the
  > assertion is self-consistent — but confirm the pushed return address matches the documented Z80 (the
  > instruction after HALT).

- [ ] **Step 2: Run to verify.**
  Run: `dotnet test --filter "FullyQualifiedName~HALT_"`
  Expected: PASS (Task 2's `_halted = false;` is the wake). If `HALT_then_IRQ` fails because `_halted` is
  not cleared or the idle path differs, FIX the partial (`_halted = false;` must run in
  `TryServiceInterrupt` before the service — it does).

- [ ] **Step 3: Full gate.**
  Run: `dotnet test` → all green.
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green.

- [ ] **Step 4: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs \
        src/CpuEmulator.Cpus.Z80/Z80Cpu.cs
git commit -m "$(cat <<'EOF'
feat(z80): HALT-then-interrupt wake UAT (services + resumes; idle burns budget, no spin)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~2.

---

### Task 8: Priority + the full-suite closeout gate (TDD + docs)

> One more UAT proving NMI beats a maskable IRQ when both are pending, then the final full-suite gate +
> the honest closeout.

**Files:**
- Test: `tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs` (extend — priority)
- Modify: `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md` (mark M3.5-1 detailed/done-pending-PR)
- Modify: `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md` (the close-state line, if it
  tracks M3.5-1)

- [ ] **Step 1: Write the failing test.** Add to `Z80InterruptServicingTests.cs`:

```csharp
    [Fact]
    public void NMI_beats_a_maskable_IRQ_when_both_pending()
    {
        var (cpu, _) = BuildCpu();
        cpu.SetRegister("PC", 0x5000); cpu.SetRegister("SP", 0xFFF0);
        cpu.Im = 1; cpu.Iff1 = true;
        cpu.SetIrqLine(true);    // maskable pending
        cpu.SetNmiLine(true);    // NMI also pending
        cpu.Step();
        Assert.Equal(0x0066u, (uint)cpu.GetRegister("PC"));   // NMI wins → 0x0066, not 0x0038
        // IFF1 cleared by NMI; the IRQ remains pending (line still high) for the next boundary.
        Assert.False(cpu.Iff1);
    }
```

- [ ] **Step 2: Run to verify.**
  Run: `dotnet test --filter "FullyQualifiedName~NMI_beats"`
  Expected: PASS (Task 2's NMI-first ordering already implements this).

- [ ] **Step 3: The full closeout gate (the M3.5-1 exit criterion).**
  Run: `dotnet test` → full suite green; record the EXACT count (baseline + the new interrupt UATs +
  the EI-delay synthetic test).
  Run: `dotnet build --no-incremental -warnaserror` → clean.
  Run: `dotnet test --filter "FullyQualifiedName~RegeneratedSpecTests"` → green (6502 byte-identical).
  Run the staged Z80 TomHarte sweep (servicing must NOT perturb the single-step path):
```bash
CPUEMULATOR_Z80_REGS_ONLY=1 dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~Z80TomHarteTests"
```
  Expected: the WHOLE Z80 ISA (base+CB+ED+DD+FD+DDCB+FDCB) **0 failures** at the universal Q/WZ/IM bar — incl.
  the `fb.json` (EI) and `f3.json` (DI) vectors green under the new EI-delay model (Task 6 Step 10).

- [ ] **Step 4: Update the scoped plan + overview.** In
  `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md`, in the M3.5-1 section, add a pointer:
  "**Detailed + implemented:** see `docs/superpowers/plans/2026-06-14-m3-z80-m35-1-interrupt-servicing.md`
  (PR #NN). Close-state: IM 0/1/2 + NMI + IFF1/IFF2 + EI-delay + HALT-wake serviced; interrupt UAT green; the
  ADR Decision-5 seam survived `Core`/dispatcher unchanged — the one enumerated generator delta is the
  `Ei`-body partial hook." Add the doc to the "Slice docs index". In the overview's status table, flip the
  "Interrupt SERVICING" row to "done (M3.5-1)" if/when the PR merges (or note "plan detailed; PR pending").

- [ ] **Step 5: Commit.**

```bash
git add tests/CpuEmulator.Tests/TomHarte/Z80InterruptServicingTests.cs \
        docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md \
        docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md
git commit -m "$(cat <<'EOF'
feat(z80): NMI-over-IRQ priority UAT + M3.5-1 closeout (interrupt servicing complete)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
EOF
)"
```

**New-test estimate:** ~1.

---

## Open the PR

- [ ] After Task 8, open the PR `feat/m3-z80-interrupt-servicing` → `main`. PR body includes a **Docs Impact**
  section (the scoped plan + overview pointers) and a **honest close-state**: what is serviced (IM0/1/2 +
  NMI + IFF + EI-delay + HALT-wake), what is gated (the deterministic interrupt UAT — list the cases), and
  the ONE enumerated generator delta (the `Ei`-body partial hook), plus the explicit non-goals (ZEXALL =
  M3.5-2, JIT = M3.5-3, no per-T-state acknowledge bus trace). Note the ADR Decision-5 finding: the seam
  survived `Core`/the dispatcher unchanged (positive), with the `Ei`-delay op-body change the only delta.

---

## Self-review (run before opening the PR)

**1. Spec coverage** (against the M3.5-1 scope):
- IM 0 (RST n) → Task 4. ✓  IM 1 (RST 38h) → Task 2. ✓  IM 2 (I-table) → Task 5. ✓  NMI (0x0066, IFF
  save/restore via RETN) → Task 3. ✓  IFF1/IFF2 gate → Tasks 1–5. ✓  EI one-instruction delay → Task 6. ✓
  DI mask → Task 6. ✓  HALT wake → Tasks 2 (`_halted = false`) + 7 (UAT). ✓  Push PC + WZ + R bump + cycle
  costs → Tasks 2–5. ✓  Priority (NMI > IRQ) → Task 8. ✓  The interrupt UAT (D5) → the whole
  `Z80InterruptServicingTests` class. ✓  TomHarte path unchanged → every task's full gate + Task 8 Step 3. ✓

**2. Placeholder scan:** every code step shows literal code; cycle constants are derived against the UAT's
exact T-count assertion (the "make the constant match" instruction is explicit, not a TODO); the EI-delay
model has a pinned recommended resolution (Task 6 Step 10) with the `fb.json` oracle named.

**3. Type/name consistency:** `_irqLine`/`_nmiLine`/`_nmiPending`/`_eiPending`/`InterruptData`/`PushPc`/
`BumpRefresh`/`OnInterruptEnable` are defined once (Tasks 1, 2, 4, 6) and referenced consistently;
`TryServiceInterrupt`/`InterruptPending` match the partial declarations in `Z80Cpu.cs:78,124`; the cycle
property is `CycleCount` (`CpuEmitter.cs:51`); the `Z80Cpu` ctor is `new Z80Cpu(bus, io)` and state is set
via `SetRegister`/`Iff1`/`Iff2`/`Im` (matching `Z80TomHarteRunner.cs:38-54`).

---

## Risks

- **No servicing vector oracle.** The interrupt UAT is hand-constructed against the Zilog manual + MAME
  cross-check, not SingleStepTests vectors (which do not cover servicing — the documented reason D5 makes the
  UAT the primary gate). The cycle counts (11/13/13/19) and the IFF save/clear rules are manual-derived; pin
  each against the table above and note in the closeout that they are reference-derived, not vector-derived.
  ZEXALL (M3.5-2) is the integration backstop, but ZEXALL itself does not exercise servicing — so the UAT is
  genuinely the only gate for the servicing PATH. Cross-check against a second emulator (MAME `z80.cpp` /
  the FUSE Z80 core) if any assertion is uncertain.
- **The EI-delay vs. the EI vector (Task 6 Step 10) is the load-bearing subtlety.** The single-step `fb.json`
  vector asserts the architectural IFF final state; the EI-delay is a SERVICING-timing concept the vectors do
  not model. The recommended resolution (set IFF immediately for the vector + a separate `_eiPending`
  no-service window) keeps BOTH oracles green — but it MUST be pinned against `fb.json` at implementation
  time. If the vector turns out to expect `iff1 = 0` after a single EI step (i.e. the TomHarte model itself
  defers), the simpler "defer the IFF write" model works and the recommended resolution is unnecessary — read
  the vector and choose.
- **Cycle-constant arithmetic** (the `_cycles += <T> - <busCharged>` form). `ReadBus`/`WriteBus` each charge
  1; `PushPc` charges 2; IM2 adds 2 more reads. The plan instructs: make the constant match the UAT's exact
  T-count assertion (the assertion is the oracle). Do not ship a guessed constant.
- **R-bump timing.** The acknowledge is one M1 cycle → R+1. If a reference shows the Z80 bumps R differently
  on an interrupt (some model R+1 for NMI and R+1 for INT identically — which this plan does), confirm
  against the manual; the UAT pins R before/after.
- **HALT post-PC.** Whether the pushed return address is the HALT opcode address or the address after it
  depends on the `Halt()` body's PC handling (Task 7 Step 1 note). Pin against the `76.json` vector's final
  PC + the documented "resume after HALT" behavior.

---

## Invariants (carried forward — every task)

- TDD task-by-task; full gate after each: `dotnet build --no-incremental -warnaserror` clean; the targeted
  tests green; the 6502 byte-identity guard `RegeneratedSpecTests` green; the WHOLE Z80 interpreter sweep
  (base+CB+ED+DD+FD+DDCB+FDCB) stays green at the universal Q/WZ/IM bar (**servicing must NOT perturb the
  single-step path** — `TryServiceInterrupt` returns false when nothing is pending, so every TomHarte case is
  unaffected; the ONE exception is the `EI`/`DI` vectors, re-validated under the new EI-delay model at Task 6
  Step 10).
- Every 6502 artifact byte-identical (the only generator change — the `Ei`-body hook + the
  `partial void OnInterruptEnable()` declaration — is structured-only, so the 6502 `.g.cs` is unchanged).
- Honest close-state: servicing implemented + interrupt UAT green; ZEXALL is M3.5-2; the JIT is M3.5-3; the
  ADR Decision-5 seam survived `Core`/dispatcher unchanged with the `Ei`-body partial hook the one
  enumerated generator delta; the cycle counts + IFF rules are reference-derived (no servicing vector
  oracle).

---

## Slice docs index

- **Scoped parent (M3.5):** `docs/superpowers/plans/2026-06-14-m3-z80-zexall-jit-m35.md` (this doc details
  its M3.5-1 section)
- **Overview / sequencing:** `docs/superpowers/plans/2026-06-14-m3-z80-finish-line-overview.md`
- **Depth template + close-state record:** `docs/superpowers/plans/2026-06-14-m3-z80-ed-core.md`
- **Architecture (Decision 5 — the interrupt model; risk-Q8 — HALT/Run):**
  `docs/architecture/0001-z80-second-architecture.md:401-447,720-723`
- **The 6502 interrupt precedent:** `src/CpuEmulator.Cpus.Mos6502/Mos6502Cpu.cs:54-107`
