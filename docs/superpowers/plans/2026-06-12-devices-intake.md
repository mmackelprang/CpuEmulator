# Devices Intake: Scheduler Teeth + Wired-OR IRQ + Timer + UART rx-IRQ + Honest Peek + Raw Terminal — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development
> (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use
> checkbox (`- [ ]`) syntax for tracking.

**Goal:** discharge the recorded device-layer intake (PR #8 closeout) plus the spec's
timer-milestone deferrals in one chunk — **interrupts and time become real**. Six fronts:
(1) **`IScheduler` grows its planned teeth** (spec §4) — `ScheduleAt` returns a
`ScheduledEvent` cancellation handle, `ScheduleEvery` repeats, and `Machine.Run` chunks CPU
slices to the next pending event (the chunk-1 recorded note, verbatim in `Machine.cs:61-62`);
(2) **wired-OR `InterruptLine`** (the `InterruptLine.cs:4-5` deferral) —
`IInterruptLine.Source()` per-device handles, line high while ANY source is; (3)
**`IntervalTimer : IPeripheral`** — a 4-register 16-bit cycle-domain timer firing at exact
cycles with a level IRQ; (4) **`SimpleUart` rx-IRQ** (intake item 1) — CTRL at the reserved
offset 2, level IRQ while rx-ready && enabled; (5) **side-effect-free Peek** (intake
item 2) — `IPeripheral.TryPeek` default interface method + `IAddressSpace.TryPeek8` + the
monitor's display reads peek where honest; (6) **raw-mode terminal** (intake item 3) — host
`--terminal` per-keystroke loop over an injectable console, Ctrl-] exits to the monitor
prompt. Capstones: an interrupt-driven echo (WAI-free), a timer-IRQ handler counting in RAM
— both monitor-assembled — and `Breadboard6502` v2 with the timer at $D100. **PR #11**
(branch `feat/devices-intake`, base `main`, head `272058e` is the baseline; 897 tests green).

**Architecture:** Core changes are the headline, but each is the *already-recorded* growth
path: `ScheduledEvent`/`ScheduleEvery` realize the §4 teeth; the `Machine.Run` chunking
realizes its own doc comment; wired-OR realizes the `InterruptLine` doc comment. One derived
addition beyond the briefs' letter (recorded below): a **device-honest time source** —
`Machine` binds `Cpu.CycleCount` so a device scheduling mid-slice (a `STA $D100` enabling
the timer halfway through a `Run` budget) schedules relative to *real* CPU time, not the
stale committed cycle. Without it, "fires at exact cycle N" is false whenever the enabling
store lands mid-slice — i.e. every CPU-programmed enable. The generated core increments
`_cycles` per bus transaction (`Mos6502Cpu.g.cs` header contract), so the write-cycle
timestamp is exact. Peripherals stays CPU-agnostic and Core-only (`IntervalTimer` joins
`SimpleUart`); the monitor's *display* reads (`m`, `d`, the `s` report prefetch) peek; its
*execution* reads and `l`/`w`/`a` stay live-bus (recorded scope decision). The terminal
loop is single-threaded by design (Ground truth F) — no cross-thread IRQ writes.

**Tech stack:** unchanged (net10.0, Roslyn 4.12.0, xUnit 2.9.3); no new projects;
`IsAotCompatible` posture unchanged (default interface methods are AOT-clean).

**Plan series:** M1 PRs #1–#10 ✅ (spine complete) · **devices intake: this plan (PR #11)**.

**NOT in scope (recorded, with where each lands):**
- **65C02 `WAI`/`STP`** (the echo is deliberately WAI-free; 65C02 opcodes land with a
  65C02 spec table, M3+) · **DMA, sound, video, GPIO/ADC/DAC** (M4+ horizon, spec §9/§11).
- **`IInterruptController` as a device** — wired-OR covers N-devices-one-pin; a prioritized
  controller device (8259-style) stays M4+ per spec §9.
- **Side-effect-free Poke** — no consumer exists: the only write perturbation on record is
  writes-over-ROM landing nothing, and bypassing ROM write-protect from the monitor is a
  *feature decision*, not a transparency fix. Monitor-v3 backlog, alongside
  **verify-after-write for `a`** — now *feasible* but not landed: the `a`-over-ROM pin and
  docs change "rejected until Peek exists" to "feasible, recorded backlog" (Task 3).
- **`i` control-byte escapes** — discharged by `--terminal` (raw keystrokes carry Ctrl
  bytes); `i` stays printable-verbatim. **Terminal re-entry (a `t` REPL command)** —
  `--terminal` is one-way into the monitor prompt; `t` was rejected in PR #8 and stays
  rejected (host-v3 on real demand).
- **Timer COUNT readback** — the 4-register window is full (CTRL, PERIODL, PERIODH,
  STATUS); an honest live countdown needs a wider window. Recorded with timer-v2 ideas.
- **Reset propagation to peripherals** (`Machine.Reset` resets the CPU only; timers tick
  across guest reset) — known M-next design question; nothing observable changes here.

**Intake honored (PR #8 closeout "Intake for the next chunks" + spec deferrals):**
1. *"rx-IRQ + timers: `SimpleUart.Realize` will claim `context.IrqLine` and STATUS grows
   an enable bit"* → Tasks 4–5, with one recorded correction: the enable bit lives in a
   new **CTRL register at the reserved offset 2**, not in STATUS (the controller brief's
   resolution — STATUS stays a pure ready-bits register).
2. *"Side-effect-free Peek/Poke API … both PINNED"* → Task 3 lands Peek (read side); the
   two perturbation pins flip under explicit authorization; the ROM write-drop pin stays
   (write side recorded out of scope, above).
3. *"Raw-mode terminal: per-keystroke input, control bytes, Ctrl-C-as-guest-break; the
   console-encoding caveat … recorded with it"* → Task 6: `--terminal`, Ctrl+C as guest
   byte 0x03 via `TreatControlCAsInput`, Ctrl-] exits, encoding caveat per Ground truth F.
4. *"Klaus-through-host throughput … ~36 ms — measure before touching the seam"* → the
   chunked `Run` adds one O(1) queue peek per slice (empty-queue path otherwise
   identical); the Task 8 gate re-measures Klaus-through-host (expected: noise).
5. Spec §4 *"`IScheduler` … grows real teeth in the timers milestone"* and *"Interrupt
   controller — deferred"* → Tasks 1–2; the spec closeout records both as
   delivered-in-part (wired-OR ≠ controller device).
6. Spec §8 *"Automated UAT is a pre-merge gate"* + docs-are-a-gate (this chunk's standing
   rule) → Tasks 7–8: feature docs ship in the same PR; `Category=UAT` grows to 8.

**Recorded deviations/departures this plan makes deliberately:**
- **The scheduler gains a device-honest time source** (`CycleScheduler.BindTimeSource`,
  internal, wired by `Machine`) — beyond the briefs' letter, derived as necessary for
  "timer fires at exact cycle N" when the enabling write lands mid-slice (Architecture);
  without it the timer is exact only when enabled from host code between runs.
- **`ScheduleAt` migrates by return-type change** (`void` → `ScheduledEvent`), not an
  additive overload: every existing call site discards the return value and compiles
  unchanged; one interface, one semantic. The chosen "your call" branch, recorded.
- **The wired-OR line forwards the computed level on EVERY input transition**, not only on
  changes. The existing pins demand it (`Reassert_while_asserted_forwards_true_again` pins
  `[true, true]`; `Release_without_assert_forwards_false` pins `[false]`) and it is safe:
  `SetIrqLine` stores level; `SetNmiLine` edge-detects against its own previous level —
  a re-presented high never fabricates an NMI edge. The OR is in the *value* forwarded.
- **Timer decisions** (the brief's "pick the simpler, document"): STATUS is
  **write-1-clear** — every timer READ stays side-effect-free, making `TryPeek` the
  identity (read-clears would reintroduce a perturbing register the day this PR abolishes
  perturbing dumps); **PERIOD == 0 means 65536** (the wrap convention — no dead enable
  state, no guest-write-triggers-host-throw hazard); **one-shot fire clears the enable
  bit**; **PERIOD writes while enabled do not retime**. All in the register table.
- **The monitor peeks display reads only** (`m` dump, `d`, the `s` report prefetch);
  `l`/`w`/`a` and execution reads stay live-bus. "Monitor `m` uses Peek" is the brief's
  floor; `d`/`s` are the same display contract; `w` (save) deliberately remains a bus
  capture — saving an MMIO region *is* a bus read sweep. Recorded, documented.
- **The interrupt UAT sessions run on a RAM-vector dev board (`IrqBoard`), not the
  `Breadboard6502`** — the breadboard's ROM owns $FFFE ("demo unchanged" is the brief's
  own constraint), so handler experiments need writable vectors. Documented: enabling the
  breadboard timer's IRQ with I clear restarts the demo; poll STATUS interactively, or
  build a RAM-vector board (building-machines.md shows how).
- **Terminal key decisions:** Ctrl-] is the escape (single keystroke, the telnet
  convention — Esc-Esc needs timing/state to disambiguate from a lone Esc byte to the
  guest); `Enter` maps to CR (0x0D) via `ConsoleKey.Enter`, not `KeyChar` (`ReadKey`
  reports '\r' on Windows, '\n' on POSIX — mapping by key keeps guest input
  platform-identical).
---

## Derived numbers (verified against the repo, not assumed)

- Baseline test count: **897** (PR #10 closeout actual, confirmed at head `272058e`).
  New-test tally (theory rows counted individually, per house convention): Task 1 ≈ 15
  net-new + 1 rewrite; Task 2 ≈ 8; Task 3 ≈ 13 net-new + 2 rewrites; Task 4 ≈ 10 net-new
  + 2 theory-row reshapes; Task 5 ≈ 20; Task 6 ≈ 11 (incl. 1 UAT); Task 7 ≈ 5 + 1
  relocation; Task 8 = 2 UAT. **Estimate: 897 + ~84 ≈ ~981.** Report actuals at closeout —
  the estimate, not the suite, is what bends.
- **`Machine.Run` chunking blast radius, walked test-by-test** (current
  `MachineRunTests.cs`): with chunk-to-next-*pending*-event (empty queue ⇒ one full-budget
  slice), `Run_advances_the_scheduler…`, `Consecutive_runs…`, `Run_with_zero_or_negative…`,
  `Run_with_a_stuck_cpu…`, `Run_returns_cycles_executed…` are untouched;
  `Run_fires_events_scheduled_within_the_budget` (event 50, budget 100) now runs as slices
  [50, 50] but asserts only `fired` — green; `Run_with_an_overshooting_cpu…` (event 103 >
  budget 100 ⇒ full slice; CPU lands 105; `AdvanceTo(105)` fires at committed 103) — all
  four asserts hold. **The only literal casualty is none — itself a finding:**
  `Run_passes_the_full_budget_to_the_cpu` stays green on an empty queue, but its name now
  pins a contract that is false the moment an event pends; the authorized rewrite narrows
  it honestly rather than leaving a lying pin.
- **Mid-slice enable timestamp:** the generated core increments `_cycles` inside
  `ReadBus`/`WriteBus` (`Mos6502Cpu.g.cs:3-5` header contract), so a `STA $D100` enable
  sees `Cpu.CycleCount` exact at its write bus-cycle, ±1 on increment-vs-dispatch
  ordering. Task 5 pins the observed ordering once
  (`Timer_enable_write_timestamp_matches_the_bus_cycle`); the UAT sessions are designed to
  be *independent* of it (run-until-target, not run-N-cycles — below).
- **Interrupt-echo cycle ledger** (Ground truth H): setup LDA# 2 + STA abs 4 + CLI 2 = 8;
  spin JMP 3/iter; per echoed byte = service 7 + LDA abs 4 + STA abs 4 + RTI 6 = 21.
  Budgets: `g $0200 100` covers setup + ~30 spins; `g 200` covers two echoes (42) + spins.
- **Timer-handler cycle ledger** (Ground truth I listing): setup 3×(LDA# 2 + STA abs 4)
  = 18 (+ CLI 2); handler = service 7 + PHA 3 + INC zp 5 + LDA# 2 + STA abs 4 + PLA 4 +
  RTI 6 = 31; loop iteration LDA zp 3 + CMP# 2 + BNE taken 3 = 8. Period $40 = 64 ≥ 31 +
  sampling latency ≤ 8 ⇒ the handler keeps up; 5 fires complete by ≈ 370 cycles ⇒ the
  `until` budget 2000 is >5× headroom. The session asserts **TargetReached at the park
  address + counter == 05** — both exact and independent of the enable-write ±1 ambiguity
  (the loop exits *because* the counter hit 5, whenever that was).
- **Wired-OR + NMI safety:** `SetNmiLine` latches only on a false→true transition of its
  *own* stored line state (`Mos6502Cpu.cs:61-63`); a second source asserting an
  already-high line re-presents `true` — no new edge, no double-latch. Pinned by
  `Second_source_asserting_a_high_line_does_not_pulse`.
- **Throughput:** chunked `Run` adds one O(1) `PriorityQueue.TryPeek` per slice — for the
  Klaus smoke, +1 failed peek per budget-1 call over the recorded ~36 ms (noise); the
  timer UAT adds an O(log n) dequeue on a queue of length 1.
- **`IrqBoard` geometry:** RAM $0000–$CFFF + UART page $D000 + timer page $D100 + RAM
  $D200–$FFFF (0x2E00 bytes; 0xD200 + 0x2E00 = 0x10000 ✓, page-aligned ✓). Vectors land
  in writable RAM; `Reset()` reads $FFFC/$FFFD = $0000 — sessions set PC via `g $0200 …`;
  Reset's S=$FD and I-set are exactly what each program's CLI is for.

## Authorized test changes (complete enumeration — anything beyond this list is a STOP)

| # | Test (current) | Change | Why it is authorized |
|---|---|---|---|
| 1 | `MachineRunTests.Run_passes_the_full_budget_to_the_cpu` | **The one authorized rewrite:** renamed/narrowed to `Run_passes_the_full_budget_when_no_events_pend` (same asserts, empty-queue scope stated) + new sibling `Run_chunks_the_slice_at_the_next_pending_event` (event at 50, budget 100 ⇒ `RunBudgets == [50, 50]`) | `Machine.Run`'s own doc comment recorded this change ("The timer milestone will chunk CPU slices to the next pending event", Machine.cs:61-62, carried from the chunk-1 plan); the controller brief pre-authorizes it |
| 2 | `SimpleUartTests.Monitor_m_command_over_DATA_dequeues` | **Replaced** by `Monitor_m_command_over_DATA_peeks_without_dequeuing` (same fixture; now asserts the dump shows 'A' AND both bytes remain queued) | The PR #8 intake pin existed to make the perturbation deliberate *until Peek lands*; Peek lands here — the pin flips to guard the fix |
| 3 | `SimpleUartTests.Bus_read_over_DATA_dequeues_rx_the_documented_monitor_perturbation` | Renamed `Bus_read_over_DATA_dequeues_rx_the_hardware_truth`; asserts byte-identical; comment reframed (the monitor no longer takes this path for dumps) | The bus-level half of the pin is *hardware truth*, not a workaround — deleting its asserts would un-pin real behavior; only its monitor framing is obsolete |
| 4 | `SimpleUartTests.Reserved_offsets_read_0x00` theory | Offset-2 row removed (offset 2 is now CTRL); offset-3 row stays | Intake item 1 — the brief assigns the reserved offset 2 to CTRL |
| 5 | `SimpleUartTests.Non_DATA_writes_are_ignored` theory | Offset-2 row removed (CTRL writes are meaningful); offsets 1 and 3 stay | Same as #4 |
| 6 | `Breadboard6502Tests.Open_bus_D100_reads_0xFF` | Relocated to `Open_bus_D200_reads_0xFF` ($D100 is now the timer; first open page is $D200) | Brief item 7: timer at $D100 on the breadboard |
| 7 | `Breadboard6502Tests.Assemble_over_rom_lands_nothing_the_documented_behavior` | Comment-only: "rejected until the Peek API exists" → "Peek exists; verify-after-write now feasible, recorded backlog". Asserts untouched | The pinned behavior is unchanged; only the recorded rationale moved |
| 8 | `Mos6502/TracingAddressSpace.cs` (test double) | Gains a delegating `TryPeek8` (mechanical: `IAddressSpace` grew a member) | Compile accommodation, not a behavior pin; the only `IAddressSpace` implementer outside Core |

Everything else in the 897 must stay green as-is — in particular all of
`CycleSchedulerTests` (each walked against the v2 scheduler), the PR #7/#8 monitor and
host pins, the demo-hello exact-equality pin, TomHarte, and Klaus.

## File structure

```
src/CpuEmulator.Core/
    ScheduledEvent.cs        — NEW (cancellation handle; Ground truth A literal)
    IScheduler.cs            — MODIFY (ScheduleAt returns ScheduledEvent; + ScheduleEvery)
    CycleScheduler.cs        — MODIFY (handles, repeat, lazy cancel, time source, TryPeekNextEventCycle)
    Machine.cs               — MODIFY (Run chunks to next pending event; BindTimeSource wiring)
    IInterruptLine.cs / InterruptLine.cs — MODIFY (+ Source(); wired-OR, Ground truth B)
    IPeripheral.cs           — MODIFY (+ TryPeek default interface method)
    IAddressSpace.cs / AddressSpace.cs   — MODIFY (+ TryPeek8)
src/CpuEmulator.Peripherals/
    SimpleUart.cs            — MODIFY (CTRL @2, rx-IRQ source, honest TryPeek)
    IntervalTimer.cs         — NEW (Ground truth D literal)
src/CpuEmulator.Monitor/
    MonitorEngine.cs         — MODIFY (PeekOrRead helper; ReadMemory/Disassemble/Step prefetch)
src/CpuEmulator.Host/
    Breadboard6502.cs        — MODIFY (timer at $D100)
    HostOptions.cs / Program.cs — MODIFY (--terminal)
    ITerminalConsole.cs / TerminalSession.cs / SystemTerminalConsole.cs — NEW (Ground truth F)
tests/CpuEmulator.Tests/
    CycleSchedulerTests.cs       — MODIFY (+cancel/repeat/time-source sections)
    MachineRunTests.cs           — MODIFY (authorized change #1 + chunking pins)
    InterruptLineTests.cs        — MODIFY (+wired-OR section; existing 4 pins untouched)
    Peripherals/SimpleUartTests.cs   — MODIFY (changes #2–#5 + CTRL/rx-IRQ tests)
    Peripherals/IntervalTimerTests.cs — NEW
    Peripherals/IrqBoard.cs          — NEW (shared RAM-vector dev-board fixture)
    PeekTests.cs                 — NEW (IPeripheral default + AddressSpace.TryPeek8)
    Monitor/MonitorPeekTests.cs  — NEW (m/d/s display reads peek; fallback path)
    Host/Breadboard6502Tests.cs  — MODIFY (changes #6–#7 + timer-page pins)
    Host/HostOptionsTests.cs     — MODIFY (--terminal rows)
    Host/TerminalSessionTests.cs — NEW (key mapping + scripted session, incl. 1 UAT)
    Host/DeviceIrqUatTests.cs    — NEW (interrupt-echo + timer-counting sessions)
    Mos6502/TracingAddressSpace.cs — MODIFY (change #8)
docs/user-guide/ — MODIFY all five: breadboard6502 (map v2, timer registers, UART CTRL,
    vector honesty) · monitor-reference (known behaviors re: Peek) · building-machines
    (scheduler teeth, wired-OR, Run chunking) · getting-started (--terminal + transcript)
    · testing (UAT count 5 → 8)
README.md                    — MODIFY (closeout: status)
docs/superpowers/specs/2026-06-11-...-design.md — MODIFY (closeout: §4 + §9 amendments)
```

## Ground truth A — scheduler API additions (tests assert these verbatim)

| Surface | Addition | Contract |
|---|---|---|
| `IScheduler.ScheduleAt` | now returns `ScheduledEvent` | one-shot at an absolute cycle; past-cycle throws `ArgumentOutOfRangeException` (unchanged); "now" is device-honest (below) |
| `IScheduler.ScheduleEvery(long interval, Action callback)` | NEW, returns `ScheduledEvent` | first fire at now + interval, then every interval; `interval <= 0` throws `ArgumentOutOfRangeException`; one handle cancels the whole chain |
| `ScheduledEvent.Cancel()` | NEW type | idempotent; safe before fire (event never runs), during its own callback (repeat chain stops), after a one-shot fired (no-op). Lazy: the queue entry is discarded when it surfaces — canceled events fire nothing and do not move committed time |
| `IScheduler.CurrentCycle` | semantics sharpened | the device-honest "now": committed time, OR the CPU's live cycle count when the machine has bound one (mid-slice device accesses see real time), OR — during event dispatch — the firing event's exact cycle (callbacks observe their own fire time) |
| `CycleScheduler.BindTimeSource` / `TryPeekNextEventCycle` | NEW, internal | machine-driver only; `Machine` binds `() => Cpu.CycleCount` after the CPU exists; the peek discards canceled heads, false on empty |
| `Machine.Run(cycles)` | slices chunked | each CPU slice runs only to the next live event (or the budget edge); empty queue = one full-budget slice, byte-identical to today. Overshoot-by-≤-one-instruction and the return contract unchanged |

## Ground truth B — wired-OR `InterruptLine` semantics

| Operation | Effect |
|---|---|
| `line.Source()` | new independent `IInterruptLine` handle; a source's `Assert`/`Release` set only its own `IsAsserted`; `source.Source()` joins the same OR |
| line level / `line.IsAsserted` | `direct OR any(source.IsAsserted)` — `direct` is the line's own `Assert`/`Release` state (back-compat: a sourceless line behaves exactly as today) |
| forwarding | EVERY input transition (line or source) forwards the **computed level** to the CPU setter — call-per-event, OR-in-the-value (preserves the existing `[true, true]` and `[false]` pins; safe because `SetNmiLine` edge-detects internally) |
| replay | unchanged — `LateBoundLine` replays the forwarded *level* at bind; a source asserted during the construction window arrives as a high level, exactly as direct asserts do today |

## Ground truth C — `SimpleUart` v2 register map (offset & 0x03; mirrors as before)

| Offset | Name | Read | Write |
|---|---|---|---|
| 0 | DATA | dequeue next rx byte; 0x00 when empty (unchanged) — **then recompute the IRQ level** | transmit via `OnTransmit` (unchanged) |
| 1 | STATUS | bit0 rx-ready, bit1 tx-ready (always 1), bits 2–7 = 0 (unchanged, still never dequeues) | ignored |
| 2 | **CTRL** (was reserved) | bit0 = rx-irq-enable; bits 1–7 = 0 | bit0 stored; other bits ignored; recomputes the IRQ level |
| 3 | — | reserved: 0x00 | ignored |

**IRQ contract (level, matching 6502 IRQ semantics):** the UART's source is asserted while
`rx-ready && rx-irq-enable`; deasserted the moment the queue drains or the enable clears.
`Realize` claims `context.IrqLine.Source()` (the PR #8 doc-comment promise, verbatim);
unrealized UARTs never touch a line — `FeedInput` stays safe. Honest peek: DATA peeks the
queue **head without dequeuing** (0x00 empty); the rest peek their read values; always true.

## Ground truth D — `IntervalTimer` register map (offset & 0x03; mirrors through its page)

| Offset | Name | Read | Write |
|---|---|---|---|
| 0 | CTRL | live bits: bit0 enable, bit1 irq-enable, bit2 repeat; bits 3–7 = 0 | bits 0–2 stored. enable 0→1: schedule the fire PERIOD cycles from now (device-honest now); enable 1→0: cancel the pending fire. irq-enable changes re-evaluate the IRQ level immediately. repeat changes apply at the next enable or fire |
| 1 | PERIODL | latched period low byte | stored; **does not retime a pending fire** |
| 2 | PERIODH | latched period high byte | stored; same |
| 3 | STATUS | bit0 fired; bits 1–7 = 0 | **write-1-clear**: bit0 set clears fired (and drops the IRQ level); writes without bit0 ignored |

**Contracts:** PERIOD is 16-bit cycles; **PERIOD == 0 means 65536** (the wrap convention).
One-shot (repeat=0): fires once at enable+PERIOD, sets fired, clears its own enable bit.
Repeat: `ScheduleEvery(PERIOD)` until disabled; clearing the repeat bit mid-flight makes
the next fire the last (the fire path cancels the chain). IRQ is level-shaped: asserted
while `fired && irq-enable`. Every read is side-effect-free (that is *why* STATUS is
write-1-clear) — `TryPeek` is the identity. `Realize` claims `Scheduler` +
`IrqLine.Source()`; enabling an unrealized timer throws (host-world composition error —
a machine-composed timer is always realized).

## Ground truth E — Peek contracts

| Layer | Member | Contract |
|---|---|---|
| `IPeripheral` | `bool TryPeek(uint offset, out byte value)` — default interface method | default `value = 0; return false` (no honest peek; callers fall back to the documented-perturbing `Read`). Implementations MUST be side-effect-free |
| `IAddressSpace` / `AddressSpace` | `bool TryPeek8(uint address, out byte value)` | memory pages: always true (RAM and ROM bytes); peripheral pages: the device's `TryPeek`; unmapped: open-bus value, true, **never throws — even in strict mode** (a peek is a debugger view, not a bus transaction) |
| `MonitorEngine` / REPL | private `PeekOrRead(uint)`; no new REPL syntax | `TryPeek8 ? value : Read8` — used by `ReadMemory` (`m`), `Disassemble` (`d`), and the `Step` report prefetch; execution reads, `LoadBytes`/`SaveBytes` (`l`/`w`), and `TryAssembleAt` stay live-bus (recorded scope). The fix is transparent: `m $D000` simply stops eating input |

## Ground truth F — terminal-mode key handling (`--terminal`)

| Input | Guest byte | Notes |
|---|---|---|
| printable `KeyChar` 0x20–0x7E | the low byte | |
| Enter | 0x0D (CR) | mapped via `ConsoleKey.Enter`, NOT `KeyChar` ('\r' on Windows, '\n' on POSIX) |
| Backspace | 0x08 | via `ConsoleKey.Backspace` |
| Tab | 0x09 | |
| Esc | 0x1B | passes through as a byte — the exit key is Ctrl-], not Esc |
| Ctrl+A … Ctrl+Z | 0x01–0x1A | `KeyChar` is already the control byte in raw mode; **Ctrl+C = 0x03 reaches the guest** (`TreatControlCAsInput = true` for the session, restored on exit) |
| **Ctrl+]** | — | **exit to the monitor prompt** (`KeyChar == 0x1D`, the telnet escape; documented in the banner line) |
| `KeyChar == 0` (arrows, F-keys, …) | — | dropped silently |

**Loop contract (single-threaded, deterministic):** drain all available keys (feeding the
UART; an IRQ-enabled UART asserts as bytes land) → `Machine.Run(sliceCycles)` (default
10,000) → repeat, until Ctrl-] (`TerminalExit.UserEscape`) or the optional `maxCycles` test
seam trips (`TerminalExit.CycleLimit`). UART tx routes to `ITerminalConsole.Write((char)b)`;
the prior `OnTransmit` sink is restored on exit. **Encoding caveat (PR #8 closeout, handled
by contract):** the byte→char cast is Latin-1-identity; the real console renders through
its codepage — honest for printable ASCII + CR/LF, documented for the rest; the injectable
abstraction keeps automated tests byte-exact, the real console gets the manual smoke.

## Ground truth G — `Breadboard6502` v2 memory map

| Range | Mapping | Notes |
|---|---|---|
| $0000–$CFFF | RAM (52 KiB) | unchanged |
| $D000–$D0FF | `SimpleUart` (1 page) | DATA $D000, STATUS $D001, **CTRL $D002** live |
| $D100–$D1FF | **`IntervalTimer` (1 page)** | CTRL $D100, PERIODL $D101, PERIODH $D102, STATUS $D103; mirrors every 4 bytes |
| $D200–$DFFF | unmapped | open-bus 0xFF (the relocated pin) |
| $E000–$FFFF | ROM (8 KiB) | **demo unchanged**; vectors still all → $E000 — an enabled timer IRQ with I clear restarts the demo (documented; RAM-vector experiments use an `IrqBoard`-style board) |

## Ground truth H — the interrupt-driven echo (monitor-assembled; the rewritten echo, WAI-free)

Runs on `IrqBoard` (RAM vectors). The main loop holds no live registers — the handler may
clobber A (contrast Ground truth I, whose handler must PHA/PLA).

| Addr | Bytes | Source (as fed to `a`) | Cycles | Role |
|---|---|---|---|---|
| 0200 | A9 01 | `LDA #$01` | 2 | rx-irq-enable value |
| 0202 | 8D 02 D0 | `STA $D002` | 4 | UART CTRL: enable rx IRQ |
| 0205 | 58 | `CLI` | 2 | Reset set I; unmask |
| 0206 | 4C 06 02 | `JMP $0206` | 3 | spin — interrupt-driven, no polling |
| 0300 | AD 00 D0 | `LDA $D000` | 4 | handler: read rx (drains; level drops when empty) |
| 0303 | 8D 00 D0 | `STA $D000` | 4 | echo it back |
| 0306 | 40 | `RTI` | 6 | level still high (more bytes) ⇒ immediate re-service |
| FFFE–FFFF | 00 03 | *(poked: `m FFFE: 00 03`)* | — | IRQ/BRK vector → $0300 |

## Ground truth I — the timer-IRQ counting program (monitor-assembled)

| Addr | Bytes | Source | Cycles | Role |
|---|---|---|---|---|
| 0200 | A9 40 | `LDA #$40` | 2 | period = $0040 (64 cycles) |
| 0202 | 8D 01 D1 | `STA $D101` | 4 | PERIODL |
| 0205 | A9 00 | `LDA #$00` | 2 | |
| 0207 | 8D 02 D1 | `STA $D102` | 4 | PERIODH |
| 020A | A9 07 | `LDA #$07` | 2 | enable \| irq-enable \| repeat |
| 020C | 8D 00 D1 | `STA $D100` | 4 | CTRL: fires every 64 cycles from this write |
| 020F | 58 | `CLI` | 2 | |
| 0210 | A5 10 | `LDA $10` | 3 | loop: read the counter |
| 0212 | C9 05 | `CMP #$05` | 2 | five fires yet? |
| 0214 | D0 FA | `BNE $0210` | 3/2 | −6 ⇒ `D0 FA` (hand-checked: $0210 − ($0214+2)) |
| 0216 | 4C 16 02 | `JMP $0216` | 3 | park — the `until` target |
| 0300 | 48 | `PHA` | 3 | the loop's A is live — preserve it |
| 0301 | E6 10 | `INC $10` | 5 | count the fire |
| 0303 | A9 01 | `LDA #$01` | 2 | |
| 0305 | 8D 03 D1 | `STA $D103` | 4 | STATUS write-1-clear ⇒ IRQ level drops |
| 0308 | 68 | `PLA` | 4 | |
| 0309 | 40 | `RTI` | 6 | handler total 24 (+7 service) ≤ period 64 ✓ |
| FFFE–FFFF | 00 03 | *(poked)* | — | IRQ/BRK vector → $0300 |

---

### Task 1: Scheduler teeth — `ScheduledEvent`, `ScheduleEvery`, device-honest time, chunked `Machine.Run` (TDD)

**Files:**
- Create: `src/CpuEmulator.Core/ScheduledEvent.cs`
- Modify: `src/CpuEmulator.Core/IScheduler.cs`, `CycleScheduler.cs`, `Machine.cs`
- Modify: `tests/CpuEmulator.Tests/CycleSchedulerTests.cs`, `MachineRunTests.cs`

- [ ] **Step 1: Branch check** — `git branch --show-current` → `feat/devices-intake`
  (created from `main` at `272058e`; this plan file is its first commit).

- [ ] **Step 2: Failing scheduler tests** (new sections appended to `CycleSchedulerTests`;
  all eleven existing facts stay byte-identical — each was walked against the v2 design at
  plan time and holds):
  - Cancellation: handle non-null, `IsCanceled` false; canceled-before-fire never runs;
    `Cancel` twice / after-a-one-shot-fired are no-ops; a canceled event among live
    same-cycle events preserves survivor FIFO; a canceled head moves no committed time.
  - `ScheduleEvery`: fires at `[10, 20, 30]` for interval 10 (dispatch-time `CurrentCycle`
    logged in the callback); `Cancel` stops the chain — also when called **inside its own
    callback** (re-enqueue-before-invoke + lazy-cancel); intervals 0 and −1 throw (2
    rows); a `ScheduleAt` and a `ScheduleEvery` on the same cycle fire in FIFO order.
  - Time source: after `BindTimeSource(() => fakeNow)`, `CurrentCycle` is
    `max(committed, fakeNow)`; `ScheduleAt` validates against that live now; during
    dispatch `CurrentCycle` reports the firing event's cycle even with the source ahead.

- [ ] **Step 3: Implement** `ScheduledEvent` (full literal — this is the contract):

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// Cancellation handle for a scheduled callback (one-shot or repeating). Cancel is
/// idempotent and safe at any time: before the fire (the event never runs), inside its
/// own callback (a repeating chain stops), or after a one-shot fired (no-op). Lazy: the
/// scheduler discards the entry when it surfaces — it fires nothing, moves no time.
/// </summary>
public sealed class ScheduledEvent
{
    internal ScheduledEvent(Action callback, long interval) =>
        (Callback, Interval) = (callback, interval);

    internal Action Callback { get; }
    internal long Interval { get; }   // repeat interval in cycles; 0 = one-shot
    public bool IsCanceled { get; private set; }
    public void Cancel() => IsCanceled = true;
}
```

  and the `CycleScheduler` v2 core (the queue element becomes the handle):

```csharp
public sealed class CycleScheduler : IScheduler
{
    private readonly PriorityQueue<ScheduledEvent, (long Cycle, ulong Seq)> _queue = new();
    private ulong _nextSeq;
    private long _committed;
    private long _dispatchCycle = -1; // ≥ 0 while a callback is running (its exact cycle)
    private Func<long>? _now;

    /// <summary>Device-honest "now": committed time; the CPU's live cycle count when one
    /// is bound (mid-slice device accesses see real time); or, during dispatch, the firing
    /// event's exact cycle (callbacks observe their own fire time).</summary>
    public long CurrentCycle =>
        _dispatchCycle >= 0 ? _dispatchCycle
        : _now is null ? _committed
        : Math.Max(_committed, _now());

    /// <summary>Machine-driver only: bind the CPU's live cycle counter (mid-slice MMIO
    /// scheduling becomes exact).</summary>
    internal void BindTimeSource(Func<long> now) => _now = now;

    public ScheduledEvent ScheduleAt(long cycle, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfLessThan(cycle, CurrentCycle);
        var evt = new ScheduledEvent(callback, interval: 0);
        _queue.Enqueue(evt, (cycle, _nextSeq++));
        return evt;
    }

    public ScheduledEvent ScheduleEvery(long interval, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        var evt = new ScheduledEvent(callback, interval);
        _queue.Enqueue(evt, (CurrentCycle + interval, _nextSeq++));
        return evt;
    }

    /// <summary>Machine-driver only: the cycle of the next live (non-canceled) event,
    /// discarding canceled heads as they surface. False when nothing is pending.</summary>
    internal bool TryPeekNextEventCycle(out long cycle)
    {
        while (_queue.TryPeek(out ScheduledEvent? head, out (long Cycle, ulong Seq) due))
        {
            if (!head.IsCanceled) { cycle = due.Cycle; return true; }
            _queue.Dequeue(); // lazy removal
        }
        cycle = 0;
        return false;
    }

    /// <summary>Advance time, firing due live callbacks in cycle order (FIFO within a
    /// cycle). Repeats re-enqueue BEFORE the callback runs: a throwing repeat callback
    /// leaves its next occurrence queued; a callback canceling its own handle stops the
    /// chain. If a callback throws, its event is consumed, committed time rests at that
    /// event's cycle, and the queue is intact.</summary>
    public void AdvanceTo(long cycle)
    {
        while (_queue.TryPeek(out ScheduledEvent? evt, out (long Cycle, ulong Seq) due)
               && due.Cycle <= cycle)
        {
            _queue.Dequeue();
            if (evt.IsCanceled)
                continue; // canceled: fires nothing, moves no time
            _committed = due.Cycle;
            if (evt.Interval > 0)
                _queue.Enqueue(evt, (due.Cycle + evt.Interval, _nextSeq++));
            _dispatchCycle = due.Cycle;
            try { evt.Callback(); }
            finally { _dispatchCycle = -1; }
        }
        if (cycle > _committed)
            _committed = cycle;
    }
}
```

  `IScheduler` gains the two signatures with doc comments matching Ground truth A; its
  "minimal in M1, grows with the timer milestone" header becomes "grown to its planned
  shape in the devices chunk". `CycleScheduler` is the only implementer (grep before
  editing — any other implementer is a STOP-and-report).

- [ ] **Step 4: Existing scheduler suite green** — all eleven prior facts pass against v2.
  Any red is a STOP: the design walk claimed each holds; fix the code, never the pins.

- [ ] **Step 5: Failing `Machine.Run` tests** (`MachineRunTests`):
  - Apply **authorized change #1**: rename `Run_passes_the_full_budget_to_the_cpu` →
    `Run_passes_the_full_budget_when_no_events_pend` (asserts byte-identical; comment cites
    this plan) and add `Run_chunks_the_slice_at_the_next_pending_event` — `FakeCpu`, event
    at 50, `Run(100)` ⇒ `RunBudgets == [50L, 50L]`, `CycleCount == 100`, event fired.
  - `Run_fires_a_chunked_event_at_its_exact_committed_cycle` — the event-at-50 callback
    logs `Scheduler.CurrentCycle` ⇒ exactly 50 (dispatch-time contract, via the machine).
  - `Repeating_event_under_Run_fires_at_exact_intervals` — `ScheduleEvery(30, …)`;
    `Run(100)` ⇒ dispatch-cycle log `[30, 60, 90]`.
  - `Canceled_event_does_not_chunk_the_slice` — schedule at 50, cancel, `Run(100)` ⇒
    `RunBudgets == [100L]` (the lazy head-discard in `TryPeekNextEventCycle`).

- [ ] **Step 6: Implement the chunked `Run`** (the whole method) plus one wiring line in
  the `Machine` ctor, phase 2, after the line binds: `_scheduler.BindTimeSource(() => Cpu.CycleCount);`

```csharp
    /// <summary>
    /// Run for a cycle budget; returns cycles actually executed (may overshoot by up to
    /// one instruction). Slices chunk to the next live event, so callbacks fire at their
    /// exact cycle and their IRQs land at the very next instruction boundary. An event
    /// scheduled MID-slice still fires at its exact cycle in scheduler time, but its IRQ
    /// reaches the CPU at the end of the running slice — latency bounded by the slice
    /// (one instruction under monitor budget-1 stepping). Empty queue = one full-budget
    /// slice (the pre-PR-#11 behavior, byte-identical).
    /// </summary>
    public long Run(long cycles)
    {
        long start = Cpu.CycleCount;
        if (cycles <= 0)
            return 0;
        long target = start + cycles;
        while (Cpu.CycleCount < target)
        {
            long before = Cpu.CycleCount;
            long sliceEnd = _scheduler.TryPeekNextEventCycle(out long eventCycle)
                            && eventCycle < target
                ? Math.Max(eventCycle, before + 1) // events at/behind the CPU: 1-cycle floor
                : target;
            long budget = sliceEnd - before;
            Cpu.Run(ref budget);
            if (Cpu.CycleCount <= before)
                throw new EmulationException(
                    $"CPU '{Cpu.Architecture}' made no progress during Run; aborting to avoid a hang.");
            _scheduler.AdvanceTo(Cpu.CycleCount);
        }
        return Cpu.CycleCount - start;
    }
```

- [ ] **Step 7: Full suite green; 0 warnings.** The monitor run-delegate pins
  (`MonitorRunDelegateTests`) and both PR #7/#8 UAT session sets are the sensitive
  dependents — budget-1 slices through the chunked loop still execute exactly one
  instruction (the `Math.Max(…, before + 1)` floor preserves the ctor contract).
  **Commit** — `feat: scheduler teeth — ScheduledEvent cancellation, ScheduleEvery, device-honest time, chunked Machine.Run`

---

### Task 2: Wired-OR `InterruptLine` (TDD)

**Files:**
- Modify: `src/CpuEmulator.Core/IInterruptLine.cs`, `InterruptLine.cs`
- Modify: `tests/CpuEmulator.Tests/InterruptLineTests.cs`

- [ ] **Step 1: Failing tests** (new wired-OR section; the existing four pins —
  `Assert_forwards_true_to_target`, `Release_forwards_false_to_target`,
  `Reassert_while_asserted_forwards_true_again` (`[true, true]`),
  `Release_without_assert_forwards_false` (`[false]`) — stay byte-identical; the
  every-call-forwarding design exists to keep them):
  - `Two_sources_hold_the_line_while_either_is_asserted` — A.Assert, B.Assert, A.Release ⇒
    line still high (last forwarded value true); B.Release ⇒ false forwarded, released.
  - `Source_handles_track_their_own_assertion` — A asserted, B not: `a.IsAsserted` true,
    `b.IsAsserted` false, independent of the line level; and
    `Source_of_a_source_joins_the_same_line` (`line.Source().Source()` joins the OR).
  - `Direct_assert_is_an_input_alongside_sources` — line.Assert + source.Assert; releasing
    either alone leaves the line high.
  - `Second_source_asserting_a_high_line_does_not_pulse` — calls log `[true, true]`, never
    a false between (the NMI-edge safety pin: the level never dips).
  - `Releasing_one_of_two_asserted_sources_forwards_the_still_high_level` — that Release
    call forwards `true` (wired-OR in the value).
  - `Machine_level_two_peripherals_share_the_irq_line` — two fakes whose `Realize` claims
    `context.IrqLine.Source()`; one asserts during Realize ⇒ the CPU double sees IRQ true
    (replay through `LateBoundLine` intact); second asserts + first releases ⇒ still true;
    both released ⇒ false.

- [ ] **Step 2: Implement** (full literal — this is the contract):

```csharp
namespace CpuEmulator.Core;

/// <summary>
/// A wired-OR interrupt line: asserted while ANY input is — its own direct Assert/Release
/// (one implicit source; single-source behavior preserved exactly) or any per-device
/// handle from <see cref="Source"/>. Every input transition forwards the COMPUTED level
/// (call-per-event; the OR lives in the value). Re-presenting a high level is safe:
/// level consumers store it idempotently; edge consumers (the 6502 NMI latch) edge-detect
/// against their own previous line state.
/// </summary>
public sealed class InterruptLine : IInterruptLine
{
    private readonly Action<bool> _setLine;
    private readonly List<SourceHandle> _sources = [];
    private bool _direct;

    public InterruptLine(Action<bool> setLine)
    {
        ArgumentNullException.ThrowIfNull(setLine);
        _setLine = setLine;
    }

    /// <summary>The computed wired-OR level.</summary>
    public bool IsAsserted { get; private set; }

    public void Assert() { _direct = true; Forward(); }
    public void Release() { _direct = false; Forward(); }

    /// <summary>Create an independent per-device handle on this line. A source's
    /// Assert/Release sets only its own state; the line stays high while any input is.</summary>
    public IInterruptLine Source()
    {
        var handle = new SourceHandle(this);
        _sources.Add(handle);
        return handle;
    }

    private void Forward()
    {
        bool level = _direct;
        foreach (SourceHandle source in _sources)
            level |= source.IsAsserted;
        IsAsserted = level;
        _setLine(level);
    }

    private sealed class SourceHandle(InterruptLine line) : IInterruptLine
    {
        public bool IsAsserted { get; private set; }
        public void Assert() { IsAsserted = true; line.Forward(); }
        public void Release() { IsAsserted = false; line.Forward(); }
        public IInterruptLine Source() => line.Source();
    }
}
```

  `IInterruptLine` gains `IInterruptLine Source();` (no default — `InterruptLine` is the
  only implementer in src and tests; re-verify with a grep). The "Single-source in M1 …"
  doc comment is replaced by the wired-OR contract.

- [ ] **Step 3: Tests pass; full suite green** (the four legacy pins plus the
  IRQ-during-Realize pin in `MachineBuilderTests` are the regression watch); 0 warnings.
  **Commit** — `feat: wired-OR InterruptLine — per-device Source() handles, level-OR forwarding`

---

### Task 3: Side-effect-free Peek — `IPeripheral.TryPeek`, `AddressSpace.TryPeek8`, monitor display reads (TDD)

**Files:**
- Modify: `src/CpuEmulator.Core/IPeripheral.cs`, `IAddressSpace.cs`, `AddressSpace.cs`
- Modify: `src/CpuEmulator.Monitor/MonitorEngine.cs`
- Modify: `src/CpuEmulator.Peripherals/SimpleUart.cs` (honest peek over the v1 registers)
- Modify: `tests/CpuEmulator.Tests/Peripherals/SimpleUartTests.cs` (authorized changes #2–#3)
- Modify: `tests/CpuEmulator.Tests/Mos6502/TracingAddressSpace.cs` (authorized change #8)
- Create: `tests/CpuEmulator.Tests/PeekTests.cs`, `tests/CpuEmulator.Tests/Monitor/MonitorPeekTests.cs`

- [ ] **Step 1: Failing core tests** (`PeekTests`):
  - `Default_TryPeek_returns_false` — a minimal `IPeripheral` fake that does not override.
  - `TryPeek8_over_ram_and_rom_returns_the_byte` (2 facts).
  - `TryPeek8_over_unmapped_returns_open_bus` (0xFF, true) and
    `…_in_strict_mode_does_not_throw` (still 0xFF, true — a peek is a debugger view, not a
    bus transaction; the doc-comment sentence is the pin).
  - `TryPeek8_over_a_peripheral_without_peek_returns_false` — `RecordingPeripheral`; also
    asserts its `Read` was NOT called (peek never silently falls back).
  - `TryPeek8_over_a_peripheral_with_peek_returns_its_value` — a fake overriding `TryPeek`.

- [ ] **Step 2: Implement the core.** `IPeripheral` (the default-interface literal):

```csharp
    /// <summary>
    /// Side-effect-free read for monitors and debuggers. Default: no honest peek — false
    /// with value 0; the caller decides whether to fall back to the (potentially
    /// perturbing) <see cref="Read"/>. Implementations MUST NOT change any device state
    /// here: no queue dequeues, no flag clears, no IRQ level changes.
    /// </summary>
    bool TryPeek(uint offset, out byte value)
    {
        value = 0;
        return false;
    }
```

  `IAddressSpace.TryPeek8` (doc = Ground truth E row 2) and the `AddressSpace`
  implementation — `Read8`'s page resolution, no strict throw:

```csharp
    public bool TryPeek8(uint address, out byte value)
    {
        address &= AddressMask;
        ref PageEntry page = ref _pages[address >> PageShift];
        if (page.Backing is not null)
        {
            value = page.Backing[page.BackingOffset + (int)(address & PageMask)];
            return true;
        }
        if (page.Handler is not null)
            return page.Handler.TryPeek(address - page.HandlerBase, out value);
        value = _options.OpenBusValue; // peeking unmapped space is side-effect-free by
        return true;                   // definition — no strict-mode throw (debugger view)
    }
```

  `TracingAddressSpace` gains the delegating member (authorized change #8).

- [ ] **Step 3: UART honest peek** (v1 registers; CTRL joins in Task 4): `TryPeek` returns
  true with DATA = `_rx.TryPeek(out var head) ? head : (byte)0x00` (the head, **no
  dequeue**), STATUS = the ready bits `Read` computes, reserved = 0. Failing tests first:
  `TryPeek_DATA_returns_head_without_dequeuing` (feed A,B; peek twice ⇒ 'A' both times;
  `Read` still yields A then B); `TryPeek_DATA_empty_returns_zero`;
  `TryPeek_STATUS_matches_read`.

- [ ] **Step 4: Monitor plumbing + the authorized pin flips.** Failing tests
  (`MonitorPeekTests` + the `SimpleUartTests` replacements):
  - Authorized change #2: `Monitor_m_command_over_DATA_peeks_without_dequeuing` — the old
    pin's fixture (`m d000 1` / `q`); now asserts the dump shows `41` AND both bytes
    remain queued. Comment: the PR #8 perturbation pin, flipped to guard the Peek fix.
  - Authorized change #3: rename the bus-level pin to
    `Bus_read_over_DATA_dequeues_rx_the_hardware_truth`; asserts unchanged; comment
    reframed (live-bus DATA reads dequeue — hardware truth; the monitor's *display* path
    no longer takes it).
  - `Disassemble_over_mmio_does_not_perturb` — `d $D000 1` style: feed bytes; disassemble
    over the UART page; queue intact.
  - `Step_report_prefetch_does_not_perturb` — PC over a recording peek-capable fake;
    `Step()`'s disassembly prefetch calls `TryPeek`, not `Read` (the fake's read counter
    stays 0 for the prefetch; it returns consistent peek/live values so execution is sane).
  - `ReadMemory_falls_back_to_live_reads_without_peek` — a no-peek recording fake: `m`
    still works and `Read` IS called (the documented fallback).
  Then implement: private `byte PeekOrRead(uint address) => _memory.TryPeek8(address, out
  byte value) ? value : _memory.Read8(address);` used in `ReadMemory`, both `Disassemble`
  read sites, and the `Step` report prefetch (3 reads). The two engine doc comments ("reads
  go through the live bus — MMIO peek semantics are monitor-v2") become "display reads peek
  where the device is honest; devices without TryPeek fall back to live reads".

- [ ] **Step 5: Tests pass; full suite green; 0 warnings.** The PR #7/#8 monitor pins are
  unaffected (RAM-backed fixtures peek the identical bytes). **Commit** —
  `feat: side-effect-free Peek — IPeripheral.TryPeek, AddressSpace.TryPeek8, monitor display reads peek`

---

### Task 4: `SimpleUart` rx-IRQ — CTRL at offset 2 + the interrupt-driven echo (TDD)

**Files:**
- Modify: `src/CpuEmulator.Peripherals/SimpleUart.cs`
- Modify: `tests/CpuEmulator.Tests/Peripherals/SimpleUartTests.cs` (authorized changes #4–#5)
- Create: `tests/CpuEmulator.Tests/Peripherals/IrqBoard.cs`

- [ ] **Step 1: Failing register tests** (apply authorized changes #4–#5 — offset 2 leaves
  the reserved/ignored theories — then add):
  - `Ctrl_write_stores_and_reads_back_bit0`; `Ctrl_write_masks_to_bit0` (0xFF ⇒ 0x01);
    `Ctrl_mirrors_through_the_page` (offset 6 == CTRL).
  - `FeedInput_without_realize_never_touches_a_line` — bare UART, enable + feed: no throw.
  - Machine-level IRQ level walk (one fixture, several facts; recording CPU double):
    enable then feed ⇒ IRQ true; partial drain ⇒ still true; full drain ⇒ false;
    disable-while-queued ⇒ false; re-enable-while-queued ⇒ true (level recomputed on CTRL
    writes, not just queue transitions); feed-before-enable ⇒ false until enabled.
  - `TryPeek_CTRL_returns_enable_bit` (the Task 3 peek extends to the new register).

- [ ] **Step 2: Implement.** The UART gains `private IInterruptLine? _irq;` and
  `private bool _rxIrqEnabled;`; `Realize` becomes
  `public void Realize(IMachineContext context) => _irq = context.IrqLine.Source();`
  (doc comment updated — the PR #8 promise is discharged). DATA reads, `FeedInput`, and
  CTRL writes all end with the one recompute:

```csharp
    private void UpdateIrqLevel()
    {
        if (_irq is null) return;         // bare (unrealized) UARTs drive no line
        if (_rxIrqEnabled && !_rx.IsEmpty) _irq.Assert();
        else _irq.Release();
    }
```

  Read offset 2 ⇒ `_rxIrqEnabled ? 0x01u : 0x00u`; write offset 2 ⇒ store bit0 +
  `UpdateIrqLevel()`. Class doc comment rewritten to Ground truth C. Recompute-on-every-
  touch is correct and simple: assert/release are idempotent at the line (Task 2).

- [ ] **Step 3: The `IrqBoard` fixture + the interrupt-driven echo end-to-end** (failing
  first). `IrqBoard.Create()` per the derived geometry (RAM $0000–$CFFF, UART page $D000,
  RAM above, `Mos6502Cpu`, engine wired through `machine.Run`). **Ordering note:** the
  timer type arrives in Task 5, so `IrqBoard` lands here with RAM $D100–$FFFF and Task 5
  moves the boundary to $D200 — both layouts page-legal; the fixture is internal, not a
  pin. The end-to-end test (`Interrupt_driven_echo_round_trips_injected_bytes`): a monitor
  session assembling Ground truth H verbatim (one `a` line per listing row, vectors via
  `m FFFE: 00 03`), then —

```
g $0200 100
i HI
g 200
q
```

  driven through `MonitorRepl` with `inject: uart.FeedInput` and a `StringBuilder` tx sink
  after `machine.Reset()`. Asserts: `tx.ToString() == "HI"` (exact — interrupt-driven, no
  polling reads anywhere), contains `injected $2 bytes`, no `? `. The test comment
  narrates: `i` asserts the level while the CPU is stopped → the next `g` services at its
  first instruction boundary → handler drains one byte per service → level drops when the
  queue empties → spin resumes.

- [ ] **Step 4: Tests pass; full suite green; 0 warnings. Commit** —
  `feat: SimpleUart rx-IRQ — CTRL register, level IRQ source, interrupt-driven echo end-to-end`

---

### Task 5: `IntervalTimer` (TDD)

**Files:**
- Create: `src/CpuEmulator.Peripherals/IntervalTimer.cs`
- Create: `tests/CpuEmulator.Tests/Peripherals/IntervalTimerTests.cs`
- Modify: `tests/CpuEmulator.Tests/Peripherals/IrqBoard.cs` (map the timer at $D100)

- [ ] **Step 1: Failing tests** (machine-backed where the scheduler matters; the fixture
  pokes registers directly between `Run` slices for cycle-exact arrangements):
  - `Enable_fires_at_exactly_period_cycles` — period 32, enable at committed cycle 0;
    `Run(31)` ⇒ STATUS fired 0; `Run(1)` ⇒ fired 1 (the chunked `Run` lands the event at
    exactly 32).
  - `Enable_write_timestamp_matches_the_bus_cycle` — CPU-programmed enable (NOP sled +
    `STA $D100`); derive the write's bus cycle from the ledger; assert the fire lands at
    write-cycle + period, pinning the ±1 ordering question once (record it in the comment).
  - STATUS: `Fired_bit_reads_back`, `Write_1_clears_fired`, `Write_0_does_not_clear`.
  - IRQ level: `Fired_with_irq_enable_asserts_source`; `Clearing_fired_deasserts`;
    `Clearing_irq_enable_while_fired_deasserts`; `Fired_without_irq_enable_stays_low`.
  - `Disable_cancels_the_pending_fire` (enable, advance halfway, disable, run past ⇒
    never fired).
  - Repeat: `Repeat_fires_at_every_period` (32 and 64, write-1-clear between);
    `One_shot_clears_its_own_enable_and_does_not_refire`;
    `Clearing_repeat_midflight_makes_the_next_fire_the_last` (the Fire-path cancel).
  - Period: `Period_zero_means_65536`; `Period_bytes_read_back` (lo/hi);
    `Period_write_while_enabled_does_not_retime`. Mirrors: `Registers_mirror_through_the_page`.
  - `TryPeek_is_the_identity_for_all_four_registers` (4 rows — peeking STATUS does not
    clear it, the write-1-clear payoff).
  - `Realize_claims_scheduler_and_irq_source` (machine composition works; enable before
    Realize throws `MachineConfigurationException`).
  - End-to-end: `Timer_irq_handler_counts_five_fires` — the Ground truth I program on
    `IrqBoard` (now with the timer at $D100; RAM boundary moves to $D200): one `a` line
    per listing row after `m 0010: 00` zeroes the counter, vectors `m FFFE: 00 03`, then:

```
g $0200 until $0216 2000
m 10 1
q
```

  Asserts: output contains `target $0216 reached` and a dump line beginning `0010: 05`;
  no `? `. (TargetReached + counter==5 are exact and independent of the enable-write ±1 —
  the derived-numbers note.)

- [ ] **Step 2: Implement** (full literal — this is the contract):

```csharp
using CpuEmulator.Core;

namespace CpuEmulator.Peripherals;

/// <summary>
/// A 16-bit cycle-domain interval timer over a 4-register window, partially decoded
/// (offset &amp; 0x03) like SimpleUart — it mirrors through whatever span it is mapped over.
///
///   offset 0  CTRL     bit0 enable · bit1 irq-enable · bit2 repeat     read: live bits
///   offset 1  PERIODL  period low byte (cycles)                        read: latched value
///   offset 2  PERIODH  period high byte                                read: latched value
///   offset 3  STATUS   bit0 fired                                      write: 1 → clear
///
/// Full semantics: the plan's Ground truth D (PERIOD 0 = 65536; enable schedules from the
/// device-honest now and cancels on clear; PERIOD writes never retime; one-shot clears
/// its own enable; STATUS write-1-clear keeps every READ side-effect-free so TryPeek is
/// the identity; IRQ level = fired &amp;&amp; irq-enable). Realize claims Scheduler +
/// IrqLine.Source(). The shipped doc comment carries the full Ground-truth-D text.
/// </summary>
public sealed class IntervalTimer : IPeripheral
{
    private IScheduler? _scheduler;
    private IInterruptLine? _irq;
    private ScheduledEvent? _pending;
    private byte _ctrl;
    private ushort _period;
    private bool _fired;

    public string Name => "timer";

    private bool Enabled => (_ctrl & 0x01) != 0;
    private bool IrqEnabled => (_ctrl & 0x02) != 0;
    private bool Repeat => (_ctrl & 0x04) != 0;
    private long EffectivePeriod => _period == 0 ? 0x10000 : _period;

    public void Realize(IMachineContext context)
    {
        _scheduler = context.Scheduler;
        _irq = context.IrqLine.Source();
    }

    public uint Read(uint offset, AccessWidth width) => (offset & 0x03) switch
    {
        0 => _ctrl,
        1 => (uint)(_period & 0xFF),
        2 => (uint)(_period >> 8),
        _ => _fired ? 0x01u : 0x00u,
    };

    /// <summary>Every timer read is side-effect-free (STATUS is write-1-clear), so the
    /// honest peek is the read itself.</summary>
    public bool TryPeek(uint offset, out byte value)
    {
        value = (byte)Read(offset, AccessWidth.Byte);
        return true;
    }

    public void Write(uint offset, AccessWidth width, uint value)
    {
        byte b = unchecked((byte)value);
        switch (offset & 0x03)
        {
            case 0: WriteCtrl(b); break;
            case 1: _period = (ushort)((_period & 0xFF00) | b); break;
            case 2: _period = (ushort)((_period & 0x00FF) | (b << 8)); break;
            default: // STATUS: write-1-clear
                if ((b & 0x01) != 0) { _fired = false; UpdateIrqLevel(); }
                break;
        }
    }

    private void WriteCtrl(byte value)
    {
        bool wasEnabled = Enabled;
        _ctrl = (byte)(value & 0x07);
        if (Enabled && !wasEnabled) Schedule();
        else if (!Enabled && wasEnabled) { _pending?.Cancel(); _pending = null; }
        UpdateIrqLevel(); // irq-enable may have changed while fired
    }

    private void Schedule()
    {
        if (_scheduler is null)
            throw new MachineConfigurationException(
                "IntervalTimer enabled before Realize — compose it via Machine.");
        _pending?.Cancel();
        _pending = Repeat
            ? _scheduler.ScheduleEvery(EffectivePeriod, Fire)
            : _scheduler.ScheduleAt(_scheduler.CurrentCycle + EffectivePeriod, Fire);
    }

    private void Fire()
    {
        _fired = true;
        if (!Repeat) // one-shot — or repeat cleared mid-flight: stop the chain,
        {            // and the enable bit clears itself
            _pending?.Cancel();
            _pending = null;
            _ctrl &= 0xFE;
        }
        UpdateIrqLevel();
    }

    private void UpdateIrqLevel()
    {
        if (_irq is null) return;
        if (_fired && IrqEnabled) _irq.Assert();
        else _irq.Release();
    }
}
```

- [ ] **Step 3: Tests pass; full suite green; 0 warnings. Commit** —
  `feat: IntervalTimer — 16-bit cycle timer, write-1-clear STATUS, level IRQ, repeat via ScheduleEvery`

---

### Task 6: Raw-mode terminal — `ITerminalConsole`, `TerminalSession`, `--terminal` (TDD)

**Files:**
- Create: `src/CpuEmulator.Host/ITerminalConsole.cs`, `TerminalSession.cs`, `SystemTerminalConsole.cs`
- Modify: `src/CpuEmulator.Host/HostOptions.cs`, `Program.cs`
- Create: `tests/CpuEmulator.Tests/Host/TerminalSessionTests.cs`
- Modify: `tests/CpuEmulator.Tests/Host/HostOptionsTests.cs`

- [ ] **Step 1: Failing key-mapping tests** (pure static surface,
  `TerminalSession.TryMapKey(ConsoleKeyInfo, out byte)` + `IsExitKey`; theory rows =
  Ground truth F): 'A' → 0x41; Enter (with both '\r' and '\n' KeyChars) → 0x0D; Backspace
  → 0x08; Tab → 0x09; Esc → 0x1B; Ctrl+C (KeyChar 0x03) → 0x03; KeyChar 0 (an arrow) →
  unmapped; Ctrl-] (KeyChar 0x1D) → `IsExitKey` true, unmapped as input.

- [ ] **Step 2: Failing session tests** (`FakeTerminalConsole`: a `Queue<ConsoleKeyInfo?>`
  script — `null` entries are one "no key available" poll each, letting a `Run` slice
  pass between keystrokes deterministically; `Output` is a `StringBuilder`; `ReadKey` on
  an exhausted script throws — a misconfigured script fails loudly, never spins):
  `Session_returns_cycle_limit_when_the_seam_trips` (one pause + small `maxCycles` ⇒
  `CycleLimit`); `Session_restores_the_previous_transmit_sink_on_exit`; and **the
  terminal UAT (the brief-required literal):**

```csharp
    [Fact]
    [Trait("Category", "UAT")]
    public void Terminal_session_echoes_typed_keys_and_exits_on_ctrl_rbracket()
    {
        // The user's --terminal flow, headless: boot the breadboard, let the demo ROM
        // print its hello, type "AB" at the echo loop, leave with Ctrl-]. The injectable
        // console keeps this byte-exact (the encoding caveat never enters); the real
        // console is covered by the captured manual-smoke transcript.
        var board = new Breadboard6502();
        board.Machine.Reset();
        var console = new FakeTerminalConsole();
        console.Type('A');
        console.Type('B');
        console.Pause(); // one empty poll: a Run slice executes before the exit key
        console.TypeControl(']'); // KeyChar 0x1D — the telnet escape

        var session = new TerminalSession(board.Machine, board.Uart, console,
                                          sliceCycles: 10_000, maxCycles: 1_000_000);
        TerminalExit exit = session.Run();

        Assert.Equal(TerminalExit.UserEscape, exit);
        Assert.Equal(DemoRom.Message + "AB", console.Output.ToString());
    }
```

  (Determinism walk: iteration 1 drains 'A','B' into the UART queue, then runs 10,000
  cycles — hello completes at 436, the echo loop transmits both bytes; iteration 2 reads
  Ctrl-] and exits. Exact equality, no sleeps, no timing.)

- [ ] **Step 3: Implement.** `ITerminalConsole` = `{ bool KeyAvailable { get; }
  ConsoleKeyInfo ReadKey(); void Write(char c); }`. `TerminalSession` ctor:
  `(Machine machine, SimpleUart uart, ITerminalConsole console, long sliceCycles = 10_000,
  long maxCycles = long.MaxValue)` — `maxCycles` is the test seam; the real host runs
  unbounded. Hook `uart.OnTransmit = b => console.Write((char)b)` saving the prior sink
  (restored in `finally`); loop: `while (console.KeyAvailable) { var key =
  console.ReadKey(); if (IsExitKey(key)) return TerminalExit.UserEscape; if (TryMapKey(key,
  out byte b)) uart.FeedInput(b); }` then `total += machine.Run(sliceCycles);` until
  `total >= maxCycles` ⇒ `TerminalExit.CycleLimit`. `SystemTerminalConsole` wraps
  `Console.KeyAvailable` / `Console.ReadKey(intercept: true)` / `Console.Write` —
  trivially thin, manual-smoke only (recorded; the one untested seam, by design).

- [ ] **Step 4: Failing `HostOptions` rows + wiring.** Rows: `--terminal` ⇒ Terminal true;
  `--terminal --demo` ⇒ error (mutually exclusive); `--terminal --load x.bin --pc $0300` ⇒
  all set (legal combo: load, then free-run from `--pc`). `HostOptions` gains the
  `Terminal` positional (mechanical arity change; only `TryParse` constructs it). Usage:
  `usage: CpuEmulator.Host [--demo | [--terminal] [--load <bin> [--at $addr] [--pc $addr]]]`.
  `Program.Main`, between the load block and the REPL: print
  `(terminal — Ctrl-] exits to the monitor)`, set `Console.TreatControlCAsInput = true`
  (Ctrl+C becomes guest byte 0x03; prior value restored in `finally`), run
  `new TerminalSession(board.Machine, board.Uart, new SystemTerminalConsole()).Run()`, then
  fall through to the existing banner + REPL launch unchanged. (`TreatControlCAsInput`
  can throw `IOException` under redirected stdin — interactive-only by nature; the throw
  surfaces as a clear error + exit 2; console side is manual smoke.)

- [ ] **Step 5: Tests pass; full suite green; 0 warnings. Commit** —
  `feat: raw-mode terminal — --terminal keystroke loop, Ctrl-] to monitor, injectable console`

---

### Task 7: `Breadboard6502` v2 + the feature docs (docs are a GATE)

**Files:**
- Modify: `src/CpuEmulator.Host/Breadboard6502.cs`
- Modify: `tests/CpuEmulator.Tests/Host/Breadboard6502Tests.cs` (authorized changes #6–#7)
- Modify: `docs/user-guide/breadboard6502.md`, `monitor-reference.md`,
  `building-machines.md`, `getting-started.md`, `testing.md`

- [ ] **Step 1: Failing breadboard tests** — apply authorized change #6 (open-bus pin
  moves to $D200) and #7 (comment-only), then add `Timer_ctrl_at_D100_reads_zero_at_boot`
  and `Timer_mirrors_at_D104` (demo-hello exactness is already pinned — no new test, just
  the gate). Implement: `Breadboard6502` gains `public IntervalTimer Timer { get; }`,
  `public const uint TimerBase = 0xD100;`, and the mapping line
  `.WithPeripheral(AddressSpaceKind.Program, TimerBase, 0x0100, Timer)`. Class doc comment
  updated to Ground truth G.

- [ ] **Step 2: Docs (each file verified against implemented behavior — quoted output is
  captured, never typed from memory):**
  - `breadboard6502.md`: memory map → Ground truth G; UART table gains the CTRL row + IRQ
    contract; new "Interval timer" section (Ground truth D); "Reset and vector behavior"
    gains the honesty paragraph — all ROM vectors → $E000, so a breadboard timer IRQ with
    I clear restarts the demo; poll STATUS interactively, or build a RAM-vector board
    (link to building-machines).
  - `monitor-reference.md` "Known behaviors": "Monitor reads perturb MMIO" REWRITTEN —
    `m`/`d`/`s` display reads are side-effect-free over devices implementing `TryPeek`
    (UART, timer); others fall back to live-bus reads; `l`/`w` remain bus sweeps by
    design. "`a`-writes over ROM": verify-after-write now feasible via peek, recorded
    backlog.
  - `building-machines.md`: `IScheduler` additions (Ground truth A + a cancel/repeat
    example), "Sharing an interrupt line" (wired-OR `Source()`, the two-device example),
    `Machine.Run` chunking note in the delegate-contract section (incl. the mid-slice-IRQ
    latency bound).
  - `getting-started.md`: "Terminal mode (--terminal)" — key table (Ground truth F),
    Ctrl-] exit, the encoding caveat, a captured transcript from the Task 8 manual smoke;
    "Known behaviors" notes Ctrl+C-is-a-guest-byte in terminal mode (REPL-mode unchanged).
  - `testing.md`: UAT session inventory 5 → 8 (named).

- [ ] **Step 3: Full suite green; 0 warnings. Commit** —
  `feat: Breadboard6502 v2 — IntervalTimer at $D100 + device-layer feature docs`

---

### Task 8: UAT sessions, the gate, closeout, PR #11

**Files:**
- Create: `tests/CpuEmulator.Tests/Host/DeviceIrqUatTests.cs`
- Modify: `README.md`, `docs/superpowers/specs/2026-06-11-cpu-emulator-framework-design.md`,
  this plan (closeout section)

- [ ] **Step 1: The two device UAT sessions** (`[Trait("Category", "UAT")]`, over
  `IrqBoard` + `MonitorRepl` — the `HostUatTests` full-REPL idiom):
  (a) `Interrupt_driven_echo_session` (Ground truth H) and
  (b) `Timer_irq_handler_counting_session` (Ground truth I) — the Task 4/5 end-to-ends ARE
  these tests, moved/tagged here, never duplicated (exactly one REPL-driven copy each).
  The terminal UAT lives in `TerminalSessionTests` (Task 6). `Category=UAT` now selects
  **8**: 2 monitor (PR #7) + 3 host (PR #8) + 3 device (this PR).

- [ ] **Step 2: Spec closeout amendments** — §4 `IScheduler` bullet records delivery
  (cancellation handles, `ScheduleEvery`, event-chunked `Machine.Run` — PR #11); §4
  interrupt-controller bullet: raw lines → "wired-OR multi-source lines (PR #11); a
  prioritized `IInterruptController` *device* remains M4+"; §9 M4+ horizon: "timers +
  interrupt controller" → "(timers + wired-OR lines DELIVERED PR #11; controller device
  remains)".

- [ ] **Step 3: README** — Status gains the device layer (timer, rx-IRQ, honest peek,
  `--terminal`); the Try-it section gains one `--terminal` line.

- [ ] **Step 4: UAT gate (commands verbatim — record outputs in the PR body):**

```
pwsh tools/get-test-vectors.ps1
pwsh tools/get-klaus.ps1
dotnet build --no-incremental          # 0 warnings required
dotnet test                            # full suite
$env:CPUEMULATOR_UAT = "full"          # standing regression insurance (spec §8)
dotnet test --filter "FullyQualifiedName~TomHarte"
Remove-Item Env:\CPUEMULATOR_UAT
dotnet test --filter "FullyQualifiedName~KlausFunctionalTests"
dotnet test --filter "Category=UAT"    # 8 sessions
# manual smoke — captured for getting-started.md, never typed from memory:
dotnet run --project src/CpuEmulator.Host -- --demo
dotnet run --project src/CpuEmulator.Host -- --terminal   # type at the echo loop, Ctrl-], q
dotnet run --project src/CpuEmulator.Host                 # spot-check: m $D000 no longer eats input
```

  Record in the PR body: full-sweep total (must equal 1,510,000 — anything else is skipped
  cases), Klaus success-trap cycle count (expected **96,241,367** — the interpreter did
  not change and the empty-queue `Run` is slice-identical; ANY drift is a STOP),
  Klaus-through-host wall time vs the recorded ~36 ms, the demo-hello exact-equality
  result, the terminal transcript, and the final count vs the ~981 estimate (actuals
  recorded, delta explained). Any red is a STOP: systematic-debugging; never weaken the
  hello equality, the stop-line formats, or the new peek/IRQ pins.

- [ ] **Step 5: Commit docs** (`docs: devices intake complete — spec §4 delivery recorded,
  README status, plan closeout`); **do NOT push** until the controller's whole-branch
  review passes. Then push, open **PR #11, base `main`**, UAT results + transcripts
  verbatim in the body; controller merges on green (standing authorization).

---

## Plan self-review (completed at write time)

- **Brief coverage, item by item:** (1) scheduler teeth — handle-returning `ScheduleAt`
  (migrate-by-return-type, recorded), `ScheduleEvery`, FIFO/determinism tests extended,
  chunked `Machine.Run`, the `Run_passes…` rewrite called out as the one authorized
  rewrite — Task 1, Ground truth A; (2) wired-OR — `Source()` handles, any-source level,
  replay preserved, two-devices-share-IRQ test — Task 2, Ground truth B;
  (3) `IntervalTimer` — write-1-clear picked-and-documented, COUNT readback
  decided-and-recorded (omitted — the window is full), `Realize` + `ScheduleEvery`, IRQ
  source, exact-cycle-N pin, $FFFE handler end-to-end via the monitor — Task 5, Ground
  truths D + I; (4) UART rx-IRQ — CTRL at offset 2 bit0, level semantics, WAI-free
  interrupt-driven echo under `Machine.Run` — Task 4, Ground truths C + H; (5) Peek —
  default-interface `TryPeek`, `AddressSpace`/monitor plumbing, honest UART/timer peeks,
  both perturbation pins replaced under authorization, no new REPL syntax — Task 3,
  Ground truth E; (6) raw terminal — `--terminal`, Ctrl-] over Esc-Esc, encoding caveat
  by contract, injectable console + manual smoke — Task 6, Ground truth F; (7) Breadboard
  v2 — timer at $D100, UART CTRL live, demo unchanged, five docs as a gate — Task 7,
  Ground truth G; (8) UAT — three new sessions + the standing gate — Tasks 6 + 8.
- **Brief-required format elements:** ground-truth tables — timer (D), UART-v2 (C),
  scheduler API (A), terminal keys (F); literal code — `ScheduledEvent`/cancellation core
  (+ full `CycleScheduler`), wired-OR `InterruptLine`, `IntervalTimer`, `TryPeek` default
  + `AddressSpace.TryPeek8`, the interrupt-driven echo (listing H + session), one
  terminal-mode UAT test; 8 TDD tasks; honest derivations — test estimate, `Machine.Run`
  blast radius (walked test-by-test in its own section), out-of-scope with landing spots.
- **Type/consistency checks, done by hand:** `PriorityQueue<ScheduledEvent,(long,ulong)>`
  peek/dequeue out-params match ✓; `ThrowIfNegativeOrZero` exists on net10.0 ✓; existing
  `ScheduleAt` call sites are expression statements — the return-type change compiles them
  unchanged ✓; `Machine` already holds the concrete `CycleScheduler` field ✓;
  `InterruptLine` / `CycleScheduler` / `TracingAddressSpace` are the only implementers of
  their interfaces (grepped; tasks re-verify) ✓; `SetNmiLine` edge-detects against its own
  `_nmiLine` — every-call level forwarding is NMI-safe ✓; default interface methods stay
  AOT-clean ✓; both listings' branch encodings hand-checked (`D0 FA` = −6) and address
  ledgers summed (echo CLI ends at $0206; timer BNE ends at $0216) ✓; the timer handler
  preserves A (PHA/PLA) where the echo handler legally clobbers it — both reasoned,
  documented ✓; `HostOptions` arity change touches only `TryParse` construction ✓.
- **Honest numbers:** baseline 897 is the controller-confirmed head count; every UAT
  budget is summed from per-instruction cycle ledgers (printed in the ground truths); the
  ~84/~981 estimate is a per-task tally, theory rows counted individually; Klaus
  96,241,367 carries forward — nothing touches the interpreter, and the empty-queue `Run`
  path is slice-identical by construction.
- **Known risks:** (a) the enable-write ±1 bus-cycle ordering is pinned once in Task 5 and
  deliberately kept OUT of the UAT assertions (run-until-target design); (b) the
  `Step`-prefetch peek test must keep peeked and live values consistent or the CPU
  executes different bytes than the report shows — the test maps a constant-value fake;
  (c) `Run_passes_the_full_budget…` staying green-as-written could tempt an implementer
  to skip authorized change #1 — the rename is required, a lying pin is a defect;
  (d) `TreatControlCAsInput` is console-environment-sensitive — kept inside the
  `--terminal`-only path with try/finally restore and manual smoke; (e) the `IrqBoard`
  RAM-boundary move between Tasks 4 and 5 is internal-fixture churn, called out in both
  tasks; (f) mid-slice-scheduled events fire exactly in scheduler time but reach the CPU
  late by up to the running slice — documented on `Machine.Run` and in
  building-machines.md, not hidden (a re-entrant slice abort is M-next machinery nothing
  currently needs).

---

## Closeout (2026-06-12)

All eight tasks complete on `feat/devices-intake` (the controller reordered Tasks 6/7:
breadboard v2 + docs landed before the terminal). G1 review (Tasks 1–4) passed with two
test-fidelity fixes folded in. Commit ladder (each independently built 0-warning and
tested green — bisect-safe):

| Commit | Content | Suite |
|---|---|---|
| `294a4b0` | Task 1: scheduler teeth — ScheduledEvent, ScheduleEvery, device-honest time, chunked Run | 917 |
| `4174b25` | Task 2: wired-OR InterruptLine — Source() handles, level-OR forwarding | 924 |
| `b1c6db4` | Task 3: Peek — IPeripheral.TryPeek, AddressSpace.TryPeek8, monitor display reads | 938 |
| `8b8e58c` | Task 4: SimpleUart rx-IRQ — CTRL @2, level IRQ source, interrupt-driven echo | 948 |
| `b4c171f` | G1 review fixes: real pins for Step-prefetch peek, machine-level wired-OR, lazy-cancel time | 948 |
| `66d5839` | Task 5: IntervalTimer — write-1-clear STATUS, level IRQ, repeat via ScheduleEvery | 972 |
| `0c94cac` | Breadboard6502 v2 — timer at $D100 + device-layer feature docs (auth. #6/#7 spent) | 974 |
| `dca0cc0` | Raw-mode terminal — --terminal, Ctrl-] to monitor, injectable console | 994 |
| *(this)* | Task 8: UAT relocation + spec/README/testing.md/closeout | 994 |

### UAT gate record (commands verbatim, outputs recorded)

```
tools/get-test-vectors + get-klaus → vectors and Klaus binary already present at
                                     ~\.cache\cpuemulator\vectors\ (full sweep + Klaus
                                     runs below prove them complete)
dotnet build --no-incremental      → Build succeeded. 0 Warning(s), 0 Error(s)
dotnet test                        → Passed! 994/994, 0 skipped

CPUEMULATOR_UAT=full dotnet test --filter "FullyQualifiedName~TomHarte"
                                   → 160/160 passed (151 opcode theory rows + 9 runner
                                     self-tests); per-row output tallied via the detailed
                                     logger: "151 × ran 10000" — TOTAL 1,510,000 =
                                     151 × 10,000, ZERO skipped cases

dotnet test --filter "FullyQualifiedName~KlausFunctionalTests"
                                   → success trap reached after 96,241,367 cycles
                                     (EXACT match to the PR #8/#10 actuals — the
                                     interpreter did not change; the empty-queue chunked
                                     Run is slice-identical, as derived)

dotnet test --filter "Category=UAT"
                                   → 8/8 passed: 2 monitor (PR #7) + 3 host (PR #8) +
                                     2 device IRQ sessions + 1 terminal session (this PR);
                                     Klaus-through-host ran live at 40 ms vs the recorded
                                     ~36 ms — noise, as the plan derived

dotnet run … -- --demo             → Hello from Breadboard6502!        (exit 0)
dotnet run … (REPL, scripted stdin)→ banner v2 + the getting-started transcript
                                     re-captured byte-verbatim; spot-check: i HI then
                                     m D000 4 shows "48 03 00 00" and the input is NOT
                                     consumed (g 200 still prints the hello — the queue
                                     held HI through the dump)
dotnet run … -- --terminal (redirected stdin)
                                   → "? --terminal needs an interactive console: …"
                                     exit 2 (the documented interactive-only posture;
                                     a human-typed interactive smoke is not possible in
                                     the headless implementation environment — the docs
                                     transcript is assembled from byte-exact captured
                                     fragments: real banner + terminal banner line +
                                     the scripted UAT's DemoRom.Message+"AB" output)
```

### Test-count actuals vs estimate

Baseline 897 → **994 actual** (+97) vs the ~981 estimate (+~84). Per task (theory rows
counted individually): T1 +20 (est ~15+1 — richer cancel/time-source coverage), T2 +7
(~8), T3 +14 net (~13+2), T4 +10 net (~10, after the two authorized row removals), G1
fixes +0 (rewrites), T5 +24 (~20 — the ±1 timestamp pin and the throw-before-Realize
split out), terminal +20 (~11 — 9 extra key-mapping theory rows), breadboard +2 net
(relocation + 2 new), T8 +0 (pure moves). Delta +13 over estimate, all richer pins —
nothing was cut.

### Deviations recap

The seven recorded at write time stand. Five added at implementation time, each recorded
in its commit message:

1. **Task 1:** the plan's `During_dispatch_CurrentCycle_reports_the_firing_event_cycle`
   test set the time source to 200 *before* `ScheduleAt(10)` — self-contradicting the
   also-pinned device-honest `ScheduleAt` validation. Fixed setup order (source jumps
   ahead *after* scheduling); the dispatch-time contract pinned is identical.
2. **Task 1:** `src/CpuEmulator.Core/AssemblyInfo.cs` (`InternalsVisibleTo`) — forced by
   the plan's own `internal` choice for `BindTimeSource`/`TryPeekNextEventCycle`;
   follows the existing `CpuEmulator.Generators` precedent.
3. **G1 review (b4c171f):** three of the new tests rewritten for fidelity — the
   Step-prefetch peek pin was vacuous, the "machine-level" wired-OR test was line-level,
   the lazy-cancel pin never reached its head. All three now revert-detecting.
4. **Task 5:** the ±1 enable-write ordering pinned as **exact-inclusive** — the generated
   core increments `_cycles` BEFORE the bus dispatch (`Mos6502Cpu.WriteBus`), so the
   timer's enable write sees `CycleCount` including its own write cycle; fire at
   write-cycle + PERIOD with no off-by-one. (The UAT sessions remain independent of it,
   as designed.)
5. **Terminal task:** the docs transcript provenance (headless environment) — assembled
   from byte-exact captured fragments rather than one human-typed session; the
   redirected-stdin error path verified against the real binary (exit 2).

Also recorded: the host banner was updated to the v2 map (not test-pinned; the README and
getting-started transcripts were re-captured from real runs), and `IrqBoard.RamLow` was
renamed `LowRamLength` (G1 reviewer note, landed with Task 5's boundary move as directed).

### Intake for the next chunks

- **Verify-after-write for `a`** + side-effect-free Poke: feasible now, monitor-v3
  backlog (feature decision, not transparency fix).
- **Timer COUNT readback**: needs a wider register window — timer-v2 ideas. Also
  timer-v2 (G2 review): **repeat-bit-set while a one-shot fire is pending** leaves the
  timer enabled-but-dormant after the fire (flags apply at the fire, nothing re-arms) —
  faithful to the plan literal, documented in breadboard6502.md; decide re-arm-at-fire
  vs document-only in timer-v2.
- **M2 design sheet (G2 review): JIT vs device-honest time + chunked Run.** Three
  pinned contracts assume interpreter granularity: (a) the write-cycle-exact enable
  timestamp needs `_cycles` current at each bus transaction — a JIT batching cycle
  updates at block exit breaks the pin; (b) chunked `Machine.Run` hands the core
  budgets as small as the gap to the next event — a block-compiled core must deopt at
  slice edges or stretch "IRQ at the very next instruction boundary" to next-block;
  (c) a short-period repeating timer fragments every `Run` into period-sized slices —
  JIT enter/exit per 64 cycles would dominate; M2 likely needs in-block event-horizon
  checks or batched slices.
- **Reset propagation to peripherals** (`Machine.Reset` resets the CPU only; timers tick
  across guest reset) — M-next design question.
- **Terminal re-entry (`t` REPL command)**: stays rejected (host-v3 on real demand).
- **`i` control-byte escapes**: discharged by `--terminal`; `i` stays printable-verbatim.
- **WAI/STP, prioritized `IInterruptController` device**: M3+/M4+ per spec.
