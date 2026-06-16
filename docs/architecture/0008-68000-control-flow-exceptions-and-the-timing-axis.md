# ADR 0008 — M4.5d: 68000 control flow, the exception model, IPL interrupts, the prefetch queue, and the deferred timing axis

> **Status:** Proposed (architecture pass — awaiting the owner's morning review before any seam-breaking implementation).
> **Date:** 2026-06-16
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect for the Coordinator while the owner is asleep; the
> `## Decisions needing the user's sign-off` block at the end gathers every fork that genuinely needs the owner's call.
> **Supersedes / relates to:**
> - **ADR 0004** (`0004-68000-decode-addressing-and-exceptions.md`) — §2 Decision 3 (the exception/privilege/IPL model:
>   the generic `TryServiceInterrupt`/`InterruptPending` seam survives; the IPL-level line is the one additive contract growth;
>   the synchronous mid-instruction vector is fallback in M4, an M6 emit item; the address-error home is the bus) + §3's M4.5d
>   scope (exceptions + privilege + interrupts). **This ADR is the implementation-shape decision for that M4.5d scope** and
>   confirms ADR 0004's decisions against the shipped M4.5a–c code.
> - **ADR 0007** (`0007-68000-interpreter-op-body-structure.md`) — the table-driven interpreter structure (option C), the §5.4
>   **seam invariant** (DO-NOT-TOUCH: `M68000FetchStream.cs`, the `M68000Cpu.cs` bus helpers, `M68000TomHarteRunner.cs`), and the
>   §6 **data/timing/exception axis split** that M4.5a–c honored. This ADR decides how M4.5d **breaks** the timing-axis half of
>   that deferral, and confirms the control-flow + exception ops can land **additively** first, without touching the seam.
> - **ADR 0003** (`0003-68000-state-width-and-bus.md`) — the `USP`/`SSP`/`SR.S`/`A7`-banking model + the wide big-endian bus this
>   ADR's exception frames + vector reads build on. The address-error alignment check (`BusAlignment.IsMisaligned`) lives there.
> - **The M4 status/resume doc** (`docs/superpowers/plans/2026-06-15-m4-status-and-resume.md`) — the M4.5d scope line (item 4)
>   + the M4.6 (68000 through the JIT) and M5 (8086) downstream.

---

## 1. Context

M4.5a (MOVE), M4.5b (integer ALU), and M4.5c (shift/rotate/bit/BCD/Scc/CMPM/data-movement) are **merged and TomHarte-green on the
data axis** (`D0–D7, A0–A6, USP, SSP, SR, RAM`, byte-exact). The interpreter executes the bulk of the ISA. Three things were
**deliberately deferred to M4.5d** by the ADR 0007 §6 axis split, and they are the entire remaining 68000-interpreter surface:

1. **The control/stack/privileged/vectoring tail** — the ops M4.5c's DC4 boundary explicitly moved here: `Bcc`/`BSR`/`DBcc`,
   `JMP`/`JSR`/`RTS`/`RTR`/`RTE`, `LINK`/`UNLK`, `TRAP`/`TRAPV`/`CHK`/`ILLEGAL`/`RESET`/`STOP`/`NOP`,
   `ANDI`/`ORI`/`EORI` `-to-CCR`/`-to-SR`.
2. **The exception model** — the vector table, the supervisor stack frame, the S-bit/USP-SSP transitions, and the synchronous
   CPU-raised exceptions: the `DIVU`/`DIVS` divide-by-zero that M4.5b/c **detect-and-defer** becomes a real exception here;
   address error (vector 3); privilege violation (vector 8); plus the `TRAP`/`TRAPV`/`CHK`/`ILLEGAL` software/check vectors.
3. **The TIMING axis** — `final.pc`, `final.prefetch`, the per-transaction bus trace, and the cycle count. These are the
   **prefetch queue's observable state**. M4.5a–c carry them with `timingAxis: false` (`M68000TomHarteRunner.cs:70`) and assert
   only the data axis. M4.5d is where the deferral comes due.

This ADR's organizing question is **risk-asymmetry**: items (1) and (2)'s *data-axis result* (where the branch lands, what frame
got pushed, which handler PC ends up in PC, what SR/USP/SSP become) is validated by the **same Step+diff runner on the same data
axis** M4.5a–c used — additive, low-risk, seam-untouched. But item (3) — asserting the **prefetch-queue refill and the cycle/trace
axis** — is **foundational and seam-breaking**: it forces a real prefetch-queue model into the fetch/bus path and a re-architected
runner. The load-bearing decision of this ADR is **to split M4.5d along exactly that fault line**, ship the additive data-axis half
first (safely, tonight), and hold the seam-breaking timing half for the owner.

### 1.1 What the shipped code already proves (verified, not assumed)

Read against `main` (M4.5a–c merged). Four facts make the additive-first split sound:

- **The dispatch seam is name-driven and additive.** `EmitMoveDispatchArms` (`CpuEmitter.cs:4262`) is a `switch` on the dataset
  operation **name** → a one-line `*Execute(...)` hook. M4.5b and M4.5c each added their families purely by (a) adding arms to that
  switch and (b) adding `partial void {name}Execute(...)` declarations to the FieldGrammar-gated emit block
  (`CpuEmitter.cs:306-338`). **The control-flow/exception dataset rows already exist** — `Bcc` (`m68000-fieldgrammar.json:73`),
  `DBcc` (`:68`), `JMP`/`JSR` (`:53/:54`), `RTS`/`RTR`/`RTE` (`:32-34`), `LINK`/`UNLK` (`:41/:42`), `TRAP`/`TRAPV`/`CHK` (`:44/:31/:59`),
  `ILLEGAL`/`RESET`/`STOP`/`NOP` (`:38/:35/:37/:36`), and the `*toCCR`/`*toSR` rows. M4.5d adds arms + bodies, **no new generator
  shape** beyond what M4.5b/c already did.
- **The stack-write substrate already works on the data axis.** `PeaExecute` (`M68000Cpu.SystemMisc.cs:51`) already does a live
  `-(A7)` push (`A7 = A7 - 4; WriteLongBus(sp, ea)`) and is **vector-green**. The `A7` accessor (`M68000Cpu.cs:52`) is a computed
  view over `USP`/`SSP` keyed by the live `SR.S` bit, and the runner **seeds and diffs both USP and SSP**
  (`M68000TomHarteRunner.cs:99-117`). So a `BSR`/`JSR` return-address push, a `LINK` frame push, and an **exception frame push** are
  all already-proven mechanisms on the data axis — they are `WriteWordBus`/`WriteLongBus` to `A7`, exactly like PEA.
- **The privilege/USP-SSP swap is free.** Because `A7` re-banks the instant the `SR.S` bit changes, an exception's "enter supervisor
  mode, push the frame on SSP" and `RTE`'s "restore SR (hence mode) and PC" need **no new banking code** — they write SR and the
  `A7` view follows. `SetSupervisorMode` (`M68000Cpu.cs:36`) exists; the real toggle is just writing SR in the body. The vector
  table is `Read32`(`mem[4·vector]`) over the existing wide big-endian bus.
- **The exception detect-and-defer heuristic is already in the runner.** `IsExceptionCase` (`M68000TomHarteRunner.cs:44`) returns the
  `DeferredException` sentinel when a case's transactions show a vector-table read pair composing to `final.pc` (the un-fakeable
  "the CPU fetched a handler and jumped" signal). M4.5a–c **deferred** these. When M4.5d **models** the exception, the same cases
  flip from deferred to asserted — *but only if the runner stops short-circuiting them.* That is the one runner change the
  exception half needs (see §3.4), and it is a small, gated edit — distinct from the large timing-axis re-architecture (§5).

### 1.2 The prefetch queue — what the vectors actually demand (the load-bearing recon)

The 680x0 v1 README (cache `680x0/v1/README.md`) and a worked case confirm the prefetch model precisely:

```
initial.prefetch = [58286, 50941]   final.prefetch = [50941, 10786]
transactions = [ ["r", 4, 6, 3076, ".w", 10786], ["n", 122] ]   length = 126
```

The 68000 keeps a **2-word prefetch queue**. `initial.prefetch[0]` is the operword (already executed-from); during the instruction
the queue **advances** — `prefetch[1]` shifts to `prefetch[0]` — and **one fresh word is fetched from the bus** to refill
`prefetch[1]` (here `10786` read from address `3076`, the single non-idle transaction). `final.pc` points one word *past* the
formal PC because the queue ran ahead. **This is the entire timing axis:** to assert `final.prefetch`, `final.pc`, the per-transaction
trace, and `length`, the emulator must **model the queue and its refill reads**, not just compute the data result. M4.5a–c sidestep
this by seeding `prefetch[0]`→`bus[pc]` and `prefetch[1]`→`bus[pc+2]` and asserting only the data result; the refill is exactly what
they defer.

---

## 2. The PR split (the load-bearing deliverable)

**M4.5d splits into two PRs along the data-axis / timing-axis fault line.** This is the central recommendation.

| | **M4.5d-1 — control flow + exceptions (data axis)** | **M4.5d-2 — timing + prefetch (cycle axis)** |
|---|---|---|
| **Scope** | `Bcc`/`BSR`/`DBcc`, `JMP`/`JSR`/`RTS`/`RTR`/`RTE`, `LINK`/`UNLK`, `TRAP`/`TRAPV`/`CHK`/`ILLEGAL`, `*toCCR`/`*toSR`, `NOP`; the exception model (vectors, frames, S-bit/USP-SSP, privilege violation, the `DIVU`/`DIVS` ÷0 vectoring, address error vector 3) | The prefetch-queue model; assert `final.pc` + `final.prefetch` + the per-transaction trace + `length` (cycle count) across **all** families (a re-run of M4.5a–d-1 vectors on the timing axis) |
| **Seam invariant** | **NOT touched** for control flow; **ONE gated runner change** to un-defer exception cases (§3.4). `M68000FetchStream.cs` + the bus helpers UNTOUCHED. | **BROKEN by construction.** Touches `M68000FetchStream.cs` (queue model), the bus helpers (refill-read cycle accounting), AND `M68000TomHarteRunner.cs` (timing assertions on by default). |
| **Validation axis** | **Data axis** (regs + SR + USP/SSP + RAM + the landed PC where the data axis already implies it) — same Step+diff M4.5a–c used | **Timing axis** (`final.pc`, `final.prefetch`, trace, `length`) |
| **Risk profile** | **LOW / additive** — new callers of proven primitives behind the name-driven dispatch seam; the exception machinery is new but localized to new partials + a small SR/stack sequence | **HIGH / foundational** — re-plumbs the fetch path that every instruction shares; a queue bug regresses the whole green sweep |
| **Vector files (verified present)** | `Bcc`, `BSR`, `DBcc`, `JMP`, `JSR`, `RTS`, `RTR`, `RTE`, `LINK`, `UNLINK`, `TRAP`, `TRAPV`, `CHK`, `NOP`, `ANDItoCCR/SR`, `ORItoCCR/SR`, `EORItoCCR/SR`, plus `DIVU`/`DIVS` (÷0 now asserts) and the exception cases inside every M4.5a–c file (un-deferred) | The same files, re-run on the timing axis |
| **Can ship ahead, autonomously tonight?** | **YES** (see §2.1) | **NO** — hold for the owner (seam break) |

### 2.1 Is there a clean additive data-axis subset (control flow + exceptions) safe to build tonight with NO seam changes? — **YES, with one caveat.**

**Control flow: unambiguously yes.** `Bcc`/`BSR`/`DBcc`/`JMP`/`JSR`/`RTS`/`RTR`/`LINK`/`UNLK`/`NOP` are the **most M4.5c-like** ops in
the whole arc — they reuse `EvaluateCondition` (the shared evaluator M4.5c built for Scc, `M68000Cpu.Scc.cs`), they push/pop `A7`
exactly like the proven `PEA`, and their data-axis result (the landed PC, the pushed/popped stack, the decremented `Dn` for `DBcc`)
is fully diffed by the existing runner. They touch **none** of the seam-protected files. This is a clean additive PR identical in
shape and risk to M4.5b/c.

**Exceptions: yes for the data-axis result, with ONE small gated runner edit that is NOT a seam break in spirit.** `TRAP`/`TRAPV`/
`CHK`/`ILLEGAL`/`RTE`/`*toSR`/privilege/÷0/address-error are new machinery, but their *result* (push the frame, enter supervisor,
vector through the table, land in the handler PC; `RTE` un-stacks SR+PC) is **data-axis-assertable** by the same Step+diff — the
frame push is `WriteLongBus` to `A7=SSP`, the vector read is `Read32`, the mode swap is an SR write. The ONE caveat: the runner today
**short-circuits** exception cases via `IsExceptionCase` → `DeferredException` (`RunCase`, `:77`). To *assert* them, M4.5d-1 must let
those cases run instead of deferring. **Recommendation: gate this behind a new `assertExceptions` flag** (default false, preserving
M4.5a–c behavior byte-for-byte; the M4.5d-1 exception sweep passes `true`). That is an **additive, opt-in** change to the runner —
it does not alter the data-axis diff logic or the fetch/bus path, and it leaves the timing-axis machinery (`timingAxis`,
`DiffBusTrace`) entirely untouched. I classify this as **inside the spirit of the seam invariant** (the runner's data-axis diff and
the fetch/bus helpers are unchanged), but because it edits a seam-listed file I flag it explicitly for the owner (sign-off item D).

> **Caveat worth stating plainly to the owner:** a few exception sub-cases are entangled with the timing axis and should be deferred
> even within M4.5d-1: **address error (vector 3) frame contents.** The 68000's bus/address-error frame is the *large* frame
> (group 0: it includes the access address, the instruction register, and a status word with the in-progress bus-cycle state). Its
> exact pushed contents depend on *where in the bus cycle* the fault occurred — a timing-coupled detail. M4.5d-1 can assert the
> **common path** (the trap is taken, supervisor entered, handler PC landed) but should **detect-and-defer the address-error frame's
> precise group-0 contents to M4.5d-2** if the data-axis diff on the pushed frame words proves timing-sensitive. The TRAP/CHK/ILLEGAL/
> privilege/÷0 frames are the *small* frame (group 1/2: SR + PC, 6 bytes) and are fully data-axis-assertable now. This keeps M4.5d-1
> clean: small-frame exceptions assert; the one large-frame case can defer its frame-content precision without blocking the PR.

### 2.2 Recommended sequence

```
M4.5d-1  control flow + exceptions   [ADDITIVE / data-axis / LOW risk]   ← safe to build autonomously tonight
   └─ M4.5d-2  timing + prefetch queue  [SEAM-BREAKING / timing-axis / HIGH risk]  ← HOLD for the owner
        └─ M4.6  68000 through the JIT (all-fallback)   [downstream — see §6]
```

Rationale for the order: M4.5d-1 completes the **functional** 68000 (every op executes correctly) without risk; M4.5d-2 then adds
**cycle accuracy** on a complete, green functional base, so any timing regression is isolated to the queue model and not confounded
with op-correctness bugs. This mirrors the M4.5a–c discipline (data axis first, always) and the ADR 0004 §3 axis split.

---

## 3. M4.5d-1 — control flow + the exception model (the additive data-axis PR)

### 3.1 Control flow (the M4.5c-like, additive core)

New hand-written partials (e.g. `M68000Cpu.Control.cs`) behind new name-driven dispatch arms. Shapes (signatures + the load-bearing
semantics, **not** full bodies):

- **`Bcc`/`BSR`/`BRA`** share the dataset row `Bcc` (`0xF000`/`0x6000`); the **condition field (bits 11-8)** sub-dispatches:
  `0000`=`BRA` (always), `0001`=`BSR` (push return PC to `-(A7)`, then branch), `0010-1111`=the 14 conditionals via
  `EvaluateCondition`. The displacement is the **8-bit field (bits 7-0)**, or, when that field is `0x00`, a following **16-bit
  displacement word** (`Bcc.w` — the branch-displacement-length half deferred from M4.4a, called out in the status doc's
  just-in-time list). `0xFF` (the `.l` form) is 68020+ → illegal on the 68000. **Data-axis result:** the landed PC + (for BSR) the
  pushed return address. `EvaluateCondition` is reused verbatim.
- **`DBcc`** (`0xF0F8`/`0x50C8`): if the condition is **false**, decrement `Dn.w` (the low 16 bits) and branch if `Dn.w != -1`;
  if true, fall through. +1 displacement word. **Data-axis result:** the decremented `Dn.w` + the landed PC. The off-by-one
  (`-1` terminates, not `0`) is the classic bug — pin it against the `DBcc` vectors.
- **`JMP`/`JSR`** (`0x4EC0`/`0x4E80`, `legalEa: Control`): compute the EA via `ComputeEa(pureEa: true)` (a control EA, never
  dereferenced — the LEA/PEA precedent), set PC to it; `JSR` first pushes the return PC to `-(A7)`.
- **`RTS`/`RTR`/`RTE`** (`0x4E75`/`0x4E77`/`0x4E73`): pop from `(A7)+`. `RTS` pops PC. `RTR` pops CCR (low byte of a popped word)
  then PC. **`RTE` is privileged** — pops SR (full 16 bits, hence mode + the X N Z V C) then PC; if `SR.S` is clear on entry → a
  privilege violation (vector 8). Writing the popped SR re-banks `A7` automatically (the USP/SSP swap is free).
- **`LINK`/`UNLK`** (`0x4E50`/`0x4E58`): `LINK An,#disp` pushes `An` to `-(A7)`, sets `An = A7`, then `A7 += disp` (a signed
  +1 word). `UNLK An` sets `A7 = An`, then pops `An` from `(A7)+`. Pure stack discipline — the `PEA` push mechanism.
- **`NOP`** (`0x4E71`): no state change (data axis trivially green; its only observable effect is timing/prefetch — M4.5d-2).

### 3.2 The exception model — vectors, frames, transitions (the new machinery)

A new hand-written helper (e.g. `M68000Cpu.Exceptions.cs`) carrying **one** `RaiseException(vector, frameKind)` routine that **all**
exception sources funnel through — this is the "integrate WITHOUT scattering" requirement. Shape:

```csharp
// The 68000 exception sequence (group 1/2 — the small frame; TRAP/CHK/TRAPV/ILLEGAL/privilege/÷0).
// Group 0 (address/bus error) uses a LARGER frame (extra access-info words) — a separate overload.
private void RaiseException(uint vector, ushort srAtFault, uint pcAtFault)
{
    // 1. Save the SR value to push (the SR captured at the point of fault, before mode change).
    // 2. Enter supervisor mode, clear the trace bit:  SR = (ushort)((srAtFault | SrSupervisorBit) & ~TraceBit);
    //    -> writing SR re-banks A7 to SSP automatically (USP/SSP swap is free).
    // 3. Push the frame on -(A7) (= -(SSP)):  push the PC (long) then the saved SR (word) — group 1/2 6-byte frame.
    // 4. Read the handler from the vector table:  PC = Read32(VectorBase + 4u * vector);   // VectorBase = 0
    // (The interpreter then resumes Step() at the new PC. CycleCount accrues per the bus accesses — exact cycle
    //  count of the sequence is the timing axis, M4.5d-2; the DATA result here is frame + mode + handler PC.)
}
```

The vector assignments (the table the routine indexes, ADR 0004 §2 Decision 3): reset=0/1 (SSP+PC; not exercised by single-step
vectors), **bus error=2, address error=3, illegal=4, divide-by-zero=5, CHK=6, TRAPV=7, privilege violation=8**, trace=9,
line-A/F=10/11, TRAP #n = 32+n (`0x4E4n`). Each source maps to its vector and calls `RaiseException`:

| Source | Op / condition | Vector | Frame group |
|---|---|---|---|
| `TRAP #n` | `0x4E4n` | 32 + n | 1/2 (small) |
| `TRAPV` | `0x4E76`, V set | 7 | 1/2 |
| `CHK` | `0x4180`, `Dn` out of `[0, bound]` | 6 | 1/2 |
| `ILLEGAL` | `0x4AFC` (+ any unmatched/illegal word) | 4 | 1/2 |
| divide-by-zero | `DIVU`/`DIVS` divisor == 0 | 5 | 1/2 |
| privilege violation | a privileged op (`RTE`, `*toSR`, `STOP`, `RESET`, `MOVE to/from SR` already in M4.5a) with `SR.S` clear | 8 | 1/2 |
| address error | a word/long access at an odd address (`BusAlignment.IsMisaligned`, ADR 0003) | 3 | 0 (large) |

> **The ÷0 promotion (the M4.5b/c detect-and-defer comes due).** M4.5b/c compute the `DIVU`/`DIVS` divisor-zero condition in the body
> and currently let the runner's `IsExceptionCase` defer the case. M4.5d-1 replaces "defer" with `RaiseException(5, …)` — the detection
> stays where it is; only the vectoring is added.

> **Privilege violation reuses an existing detection point.** `MOVE to/from SR` / `MOVE USP` (M4.5a) already gate on supervisor mode.
> M4.5d-1 makes that gate call `RaiseException(8, …)` instead of (currently) executing unconditionally — and adds the same gate to
> `RTE`/`*toSR`/`STOP`/`RESET`. This is the privilege model "integrating without scattering": one `RaiseException`, called from each
> privileged op's mode check.

### 3.3 `*toCCR` / `*toSR` (the immediate-to-system-byte ops)

`ANDItoCCR`/`ORItoCCR`/`EORItoCCR` (`0x023C`/`0x003C`/`0x0A3C`) AND/OR/EOR an immediate byte into the CCR (low byte of SR) —
unprivileged. `ANDItoSR`/`ORItoSR`/`EORItoSR` (`0x027C`/`0x007C`/`0x0A7C`) do the same to the **full 16-bit SR** — **privileged**
(privilege violation if `SR.S` clear; and writing SR may change `S`, re-banking `A7`). Vector files exist for all six. Additive bodies.

### 3.4 The one runner change M4.5d-1 needs (sign-off item D)

`M68000TomHarteRunner.RunCase` (`:77`) currently returns `DeferredException` for any `IsExceptionCase`. M4.5d-1 adds an opt-in flag:

```csharp
public static string? RunCase(M68000TomHarteCase c, bool timingAxis = false, bool assertExceptions = false)
{
    if (!assertExceptions && IsExceptionCase(c)) return DeferredException;  // M4.5a-c behavior preserved by default
    // ... seed state, Step(), diff the data axis (now including the exception result when assertExceptions) ...
}
```

The M4.5d-1 exception sweep passes `assertExceptions: true`; the M4.5a–c sweeps keep the default and stay byte-identical. **This is
the entire runner delta for M4.5d-1.** It does **not** touch `DiffBusTrace`, the `timingAxis` path, the fetch stream, or the bus
helpers. (When the address-error large-frame contents prove timing-sensitive per the §2.1 caveat, keep `IsExceptionCase` deferring
*just* the address-error subset by an extra predicate, and assert the small-frame exceptions — a one-line refinement.)

---

## 4. IPL interrupts + the interrupt-acknowledge sequence

**Recommendation: model the IPL machinery in M4.5d-2 (or a thin M4.5d-1 stub), NOT as a M4.5d-1 data-axis assertion — because the
680x0 v1 single-step dataset does not exercise asynchronous interrupts.** The vector files are all *instruction* cases; there is no
"an interrupt fires mid-stream" file. So there is nothing to assert the IPL policy against on the data axis. The right scope:

- **The seam is already generic and correct** (ADR 0004 §2 Decision 3, confirmed against `CpuEmitter.cs:202` — `Step` calls
  `TryServiceInterrupt()` first). The M68000 partial's `TryServiceInterrupt` is currently inert (`M68000Cpu.cs:118` returns false).
- **The contract growth is the IPL level** (ADR 0004's "one likely contract nudge"): a 3-bit IPL input (0–7) compared against the
  `SR` interrupt mask (bits 10-8); level 7 is non-maskable. When `IPL > mask` (or `== 7`), `TryServiceInterrupt` runs the
  acknowledge sequence: enter supervisor, push the (PC, SR) frame, set the mask to the serviced level, read the
  **autovector** (vectors 25–31 for levels 1–7) or a device-supplied vector, jump to the handler. **This reuses `RaiseException`**
  (the same frame push + vector read) — the interrupt is "an exception sourced by the IPL line," so it funnels through the same
  routine. The autovector-vs-device-vector detail (ADR 0004 §4 just-in-time item 3) defaults to **autovector** (the common case);
  the device-vector path is a partial-implementation detail, not a seam change.
- **Why M4.5d-2 (or stub):** since no vector asserts it, the IPL model is validated only by **synthetic unit tests** (assert the
  acknowledge sequence fires at the right level, pushes the right frame, lands at the autovector). It is honest to ship it as
  synthetic-tested in M4.5d-1 (clearly labeled, like M4.5b's immediate forms) OR to defer it to M4.5d-2 where the timing-aware bus
  model makes the acknowledge-cycle trace meaningful. **I lean to a thin M4.5d-1 stub** (the IPL-level input + the
  `TryServiceInterrupt` policy calling `RaiseException`, synthetic-tested) so the functional model is complete, with cycle-accuracy
  of the acknowledge sequence finished in M4.5d-2. **Flag this scope choice for the owner (sign-off item E).**

---

## 5. The TIMING axis — the seam-breaking magnitude + risk (M4.5d-2)

This is the architecturally-heavy part. Honest assessment of what enabling `final.pc`/`final.prefetch`/trace/cycle requires, and
which seam-protected files it forces.

### 5.1 What the timing axis demands

To assert `final.prefetch`, `final.pc`, the per-transaction trace, and `length`, the emulator must **model the 2-word prefetch
queue and its refills** (§1.2). Concretely, every instruction's fetch must:

1. Maintain a 2-word queue seeded from `initial.prefetch`.
2. Execute from `prefetch[0]`, advance the queue (`prefetch[1]`→`[0]`), and issue **refill reads** at the right points in the
   instruction (the 68000 refills the queue as it decodes/executes — the refill reads are interleaved with operand accesses and
   appear in `transactions`).
3. Produce `final.prefetch` = the queue's end state and `final.pc` = the formal PC (which trails the queue by the prefetch depth).
4. Charge cycles per the real bus timing (the `["n", N]` idle runs + the `["r"/"w", cycles, …]` accesses) so `CycleCount == length`.

### 5.2 Which seam-protected files it forces — HONEST verdict

| Seam-protected file | M4.5d-2 impact | Magnitude |
|---|---|---|
| **`M68000FetchStream.cs`** | **BROKEN.** Today it is a stateless `Read16`-walk from an origin (`:25-32`). The queue is *stateful* (it persists across instructions and refills mid-instruction). The fetch must become a **prefetch-queue object** that the CPU owns across Steps, not a per-instruction throwaway stream. This is a structural rewrite of the fetch model. | **HIGH** |
| **The `M68000Cpu.cs` bus helpers** (`ReadWordBus`/etc., `:72-110`) | **CHANGED.** They charge a flat `WordAccessCycles = 4` today. Real timing has variable access lengths + idle cycles + the refill reads, and the *order* of refills vs. operand accesses must match the trace. The cycle-charging model must be reworked to emit the exact transaction sequence. The generated `Step` arm's `_cycles += __stream.UnitsConsumed * 4` (`CpuEmitter.cs:231`) is part of this and is **generator** code — so M4.5d-2 also touches the generator's FieldGrammar Step emit. | **HIGH** |
| **`M68000TomHarteRunner.cs`** | **CHANGED (by design).** `timingAxis` flips on for all families; `DiffBusTrace` (`:146`, already written) becomes the gate. The runner must also stop seeding `prefetch[0]→bus[pc]` as a data-axis shim and instead seed the real queue. | **MEDIUM** (the diff code exists; the seeding changes) |
| `M68000Cpu.Move.cs` (seam-listed) | Likely untouched (the op bodies don't change; the fetch/timing wraps around them). | LOW |

**Verdict: the timing axis genuinely breaks the seam invariant — specifically `M68000FetchStream.cs` (a stateful-queue rewrite) and
the bus-helper/Step cycle model.** This is foundational: it re-plumbs the fetch path *every* instruction shares, so a bug regresses
the entire green sweep at once. It is **not** safely autonomous; it is the PR to hold for the owner.

### 5.3 How far to take cycle-accuracy in M4.5d-2 (a sub-fork — sign-off item C)

Two tiers:
- **(i) Full per-transaction cycle accuracy** — model the queue + refills + idle runs so `DiffBusTrace` and `CycleCount == length`
  pass for all ~120 files. This is the complete timing axis and what the vectors can prove. Largest effort; matches WinUAE-class
  fidelity for the single-step model.
- **(ii) `final.pc` + `final.prefetch` only (queue state, not full trace)** — model the queue's *end state* (enough to assert the two
  prefetch words + the trailing PC) but treat the cycle count / per-access trace as a later refinement. Smaller; gets the
  architecturally-novel piece (the queue) without the full idle-cycle accounting.

**Recommendation: (i), but staged** — land the queue model + `final.pc`/`final.prefetch` first (the structural seam break), then the
full trace/cycle accounting as a follow-on commit within M4.5d-2. This sequences the *foundational* change (the queue, which forces
the fetch rewrite) ahead of the *accounting* change (idle cycles), so the seam break is reviewed in isolation. The owner may prefer
(ii) as the M4.5d-2 ceiling and push full cycle accuracy to a later milestone — flagged.

---

## 6. Cross-cutting — M4.6 (JIT) and the benchmark timing axis

- **M4.6 (68000 through the JIT, all-fallback) depends on M4.5d-1, not necessarily M4.5d-2.** M4.6 wraps the CPU in
  `JittedCpu<M68000Cpu>` and proves byte-identical tier parity (data axis). Since the 68000 is **all-fallback** in M4 (ADR 0003
  Decision 3, ADR 0004 §2 Decision 3 — "exception-capable ops are `NeedsFallback` first"), the synchronous mid-instruction vector
  (TRAP/÷0/address-error) is automatically handled by the fallback valve: an exception-capable op bails to the interpreter. **So
  M4.6 needs the functional exception model (M4.5d-1) but is agnostic to the prefetch/timing axis (M4.5d-2)** — the JIT parity gate
  is the data axis. This means **M4.6 can proceed after M4.5d-1 even if M4.5d-2 is still held for the owner.** That is a scheduling
  win: the additive PR unblocks the JIT bring-up; the seam-breaking PR is not on the critical path to M4.6.
  - The one M6 (not M4) design item ADR 0004 §2 Decision 3 flagged remains: whether the *pervasive* alignment check on every
    word/long access needs emitted-IL handling rather than fallback to keep the JIT worthwhile. M4.5d-1's address-error model gives
    M6 the concrete fallback-trigger shape to reason about. Out of scope here.
- **The benchmark timing axis (the bench plan's M6 re-measure) consumes M4.5d-2.** A cycle-accurate 68000 is the prerequisite for any
  meaningful 68000 cycle/throughput benchmark. Until M4.5d-2 lands, 68000 benchmarks can measure *instructions/sec* (data-axis
  correct) but **not cycles/sec or cycle-accurate workload timing**. The bench plan's M6 re-measure should gate its 68000 cycle
  numbers on M4.5d-2; the instruction-throughput numbers can use the M4.5d-1 functional core. **Flag the dependency so the bench plan
  doesn't quote 68000 cycle figures before the prefetch model exists.**

---

## 7. Consequences

**Good.**
- M4.5d-1 completes the **functional** 68000 (every op executes, every exception vectors) additively — same risk class as M4.5b/c,
  seam untouched, safe to build autonomously. It unblocks M4.6 (the JIT) without waiting on the hard timing axis.
- One `RaiseException` routine funnels every exception/interrupt source (TRAP/CHK/TRAPV/ILLEGAL/÷0/privilege/address-error/IPL) — the
  "integrate without scattering" requirement met by a single sequence + a per-source vector mapping.
- The USP/SSP swap, the stack-frame push, and the privilege gate **reuse already-green mechanisms** (the `A7` re-bank, the PEA push,
  the MOVE-to-SR mode gate) — minimal new surface.
- The timing axis is isolated to its own PR, so its seam break is reviewed once, on a complete green functional base.

**Bad / accepted costs.**
- M4.5d-1 adds one opt-in flag to the seam-listed runner (`assertExceptions`) — a seam-listed file is edited, even if the data-axis
  diff and fetch/bus path are unchanged. Flagged for sign-off (item D).
- The IPL model has **no vector to assert it** on the data axis — it ships synthetic-tested (honest disclosure, like M4.5b's
  immediate forms). Flagged (item E).
- The address-error (group-0) large frame's exact contents may be timing-coupled; M4.5d-1 may have to defer the frame-content
  precision to M4.5d-2 (assert only that the trap is taken). Flagged in §2.1.
- M4.5d-2 genuinely breaks the ADR 0007 seam invariant (the fetch-stream rewrite + the bus cycle model). This is unavoidable — the
  timing axis *is* the prefetch-queue model — but it is the highest-risk PR of the whole 68000 arc. Held for the owner.

**Reversibility.** M4.5d-1 is fully reversible/additive (new partials + dispatch arms + an opt-in runner flag). M4.5d-2 is the
foundational one; once the fetch stream becomes a stateful queue, reverting means restoring the stateless stream — mechanical but
touches the shared fetch path. Recommend M4.5d-2 land behind the `timingAxis` flag staying default-off until the full sweep is green,
so an in-progress queue model never regresses the M4.5d-1 data-axis gate.

---

## 8. Open questions

1. **The `transactions` field-2 cycle semantics for multi-access instructions.** ADR 0004 §5 flagged the tuple's field 2 as the
   per-transaction cycle length; the README confirms it. But the *interleaving* of refill reads with operand accesses (which read is
   the prefetch refill vs. the operand fetch) must be reverse-engineered per instruction-class against the trace in M4.5d-2. Resolve
   empirically against the traces; do not pre-commit a refill-point model.
2. **Autovector vs. device-supplied interrupt vector** (ADR 0004 §4 item 3). Default autovector; confirm against any interrupt
   behavior the dataset implies (likely none — single-step). Settle in M4.5d-2.
3. **Does `final.pc` alone (tier ii, §5.3) provide enough value to ship before full cycle accuracy?** Depends on the bench plan's
   needs. Owner's call (sign-off item C).
4. **The address-error frame contents** — group-0 frame fidelity on the data axis vs. deferral to M4.5d-2 (§2.1). Resolve when the
   `MOVE`/ALU files' address-error cases are un-deferred and the pushed-frame diff is observed.

---

## Decisions needing the user's sign-off

Every fork below is stated with my recommendation, the alternative, and the risk. These are staged for the morning review; the
Coordinator may proceed on **A** and **B** autonomously tonight (they are additive/low-risk) and should **hold C, D-as-extended, F**
for the owner.

**A. Ship M4.5d-1 (control flow + exceptions) as a clean additive data-axis PR, ahead of and separate from the timing axis.**
- *Recommendation:* **Yes — build it autonomously tonight.** It is the most M4.5c-like work in the arc, reuses proven primitives
  (`EvaluateCondition`, the PEA push, the A7 re-bank), touches none of the fetch/bus seam, and validates on the same data-axis
  Step+diff. Risk: **LOW / additive.**
- *Alternative:* fold control flow + exceptions + timing into one M4.5d PR. *Rejected* — confounds a low-risk additive change with the
  highest-risk seam break, and blocks the functional completion (and M4.6) on the hard timing axis.

**B. Funnel every exception/interrupt source through ONE `RaiseException(vector, frame)` routine** (TRAP/CHK/TRAPV/ILLEGAL/÷0/
privilege/address-error/IPL), with a per-source vector mapping and the small (group 1/2) vs. large (group 0) frame split.
- *Recommendation:* **Yes.** It is the "integrate without scattering" requirement; the privilege gate + ÷0 detection already exist and
  just call into it. Risk: **LOW.**
- *Alternative:* per-op inline exception sequences. *Rejected* — scatters the frame/vector/mode logic, multiplying the highest-bug-
  density code (mirrors ADR 0007's CCR-centralization rationale).

**C. The timing axis (M4.5d-2): full per-transaction cycle accuracy (tier i) vs. `final.pc`+`final.prefetch` only (tier ii); and
staging the queue model ahead of the cycle accounting.**
- *Recommendation:* **Tier (i), staged** — land the prefetch-queue model + `final.pc`/`final.prefetch` first (the seam break), then
  the full trace/cycle accounting as a follow-on. Gets the architecturally-novel queue reviewed in isolation, then completes the
  accounting. Risk: **HIGH / foundational** (the fetch-path rewrite).
- *Alternative:* tier (ii) as the M4.5d-2 ceiling, deferring full cycle accuracy to a later milestone. Acceptable if the bench plan
  only needs queue state + instruction throughput, not cycle/sec, before then.
- **Hold for the owner** — this is the seam-breaking PR; do not build autonomously.

**D. The M4.5d-1 runner change: add an opt-in `assertExceptions` flag to `M68000TomHarteRunner.RunCase` (a seam-listed file).**
- *Recommendation:* **Approve the opt-in flag.** It is additive (default false preserves M4.5a–c byte-for-byte), it does not touch the
  data-axis diff logic, the fetch stream, the bus helpers, or the `timingAxis`/`DiffBusTrace` path — so it is **within the spirit** of
  the seam invariant even though it edits a listed file. Risk: **LOW.**
- *Alternative:* leave the runner untouched and assert exceptions in a *separate* runner. *Rejected* — duplicates the seed/diff logic;
  the opt-in flag is the minimal change.
- **Flagged because it edits a seam-listed file** — I judge it safe and in-spirit, but the seam invariant names this file, so the
  owner should bless the edit. (The Coordinator may treat this as part of A if the owner pre-approves the spirit-test.)

**E. The IPL interrupt model: ship a thin synthetic-tested stub in M4.5d-1 vs. defer entirely to M4.5d-2.**
- *Recommendation:* **Thin M4.5d-1 stub** — the IPL-level input + `TryServiceInterrupt` policy calling `RaiseException`, validated by
  synthetic unit tests (the dataset has no async-interrupt vector to assert it). Completes the functional model; cycle-accuracy of the
  acknowledge sequence finishes in M4.5d-2. Honest disclosure that it is synthetic-tested (the M4.5b immediate-forms precedent).
- *Alternative:* defer the whole IPL model to M4.5d-2 (where the timing-aware bus makes the acknowledge trace meaningful). Acceptable;
  keeps M4.5d-1 purely vector-gated.
- *Risk:* **LOW** either way (no vector pressure). Owner's preference on whether "functional-complete with a synthetic-tested IPL"
  or "vector-gated only" is the M4.5d-1 bar.

**F. The address-error (group-0) frame on the data axis: assert the full large-frame contents in M4.5d-1, or assert only "trap taken"
and defer the precise frame contents to M4.5d-2.**
- *Recommendation:* **Defer the precise group-0 frame contents to M4.5d-2** (assert the common path — trap taken, supervisor entered,
  handler landed — in M4.5d-1; refine the access-info/status-word frame words when the timing model exists, since they encode
  in-progress bus-cycle state). Keeps M4.5d-1 clean: small-frame exceptions (TRAP/CHK/TRAPV/ILLEGAL/÷0/privilege) fully assert now.
- *Alternative:* attempt the full group-0 frame in M4.5d-1. Risk that the frame's status word is timing-coupled and the data-axis diff
  on the pushed words fails for reasons that are really M4.5d-2's. 
- *Risk:* **MEDIUM** if attempted in M4.5d-1; **LOW** if deferred. Resolve empirically once the un-deferred address-error cases are
  observed.

---

*End of ADR 0008. M4.5d-1 (control flow + exceptions) is a clean additive data-axis PR (reuses `EvaluateCondition` + the proven A7
push + the A7 re-bank; one opt-in runner flag) safe to build ahead of the timing axis; M4.5d-2 (prefetch queue + cycle/trace) is the
seam-breaking foundational PR to hold for the owner. M4.6 (the JIT) depends on M4.5d-1's functional model, not on M4.5d-2. Designer:
no UX surface (headless framework). Planner can expand M4.5d-1's §3 task shape from here once the owner signs off A/B/D.*
