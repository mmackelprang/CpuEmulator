# Breadboard6502

The `Breadboard6502` is the canonical pre-wired 6502 machine that `CpuEmulator.Host` boots. It composes a MOS 6502 CPU, 52 KiB of RAM, a UART with an rx interrupt, a 16-bit interval timer, and an 8 KiB demo ROM assembled at startup by the generated single-instruction assembler.

Source: `src/CpuEmulator.Host/Breadboard6502.cs`, `src/CpuEmulator.Host/DemoRom.cs`

---

## Memory map

| Range | Size | Mapping | Notes |
|---|---|---|---|
| `$0000`–`$CFFF` | 52 KiB | RAM | Zero page (`$00`–`$FF`), stack (`$0100`–`$01FF`), user program space (house origin `$0200`) |
| `$D000`–`$D0FF` | 256 bytes | `SimpleUart` | DATA `$D000`, STATUS `$D001`, CTRL `$D002`; mirrors every 4 bytes through the page (partial decode) |
| `$D100`–`$D1FF` | 256 bytes | `IntervalTimer` | CTRL `$D100`, PERIODL `$D101`, PERIODH `$D102`, STATUS `$D103`; mirrors every 4 bytes |
| `$D200`–`$DFFF` | ~3.5 KiB | Unmapped | Open-bus reads return `0xFF`; writes ignored |
| `$E000`–`$FFFF` | 8 KiB | ROM | Demo ROM (print loop + echo loop); reset/NMI/IRQ vectors all → `$E000` |

The board uses a 16-bit address space (`addressBits: 16`). All addresses wrap at `$FFFF`.

---

## UART register reference

The `SimpleUart` is mapped at `$D000`–`$D0FF` with partial decode: the device decodes `offset & 0x03`, so all four registers mirror 64 times through the page. `$D004` is DATA again; `$D005` is STATUS; `$D0FC` is DATA; and so on.

| Offset (`& 0x03`) | Address | Name | Read | Write |
|---|---|---|---|---|
| 0 | `$D000` | DATA | Dequeue next rx byte; **`0x00` when queue is empty**; recomputes the IRQ level | Transmit: invoke `OnTransmit` with the low byte |
| 1 | `$D001` | STATUS | Bit 0 = rx-ready (queue non-empty); bit 1 = tx-ready (**always 1** — transmit is instantaneous); bits 2–7 = 0 | Ignored |
| 2 | `$D002` | CTRL | Bit 0 = rx-irq-enable; bits 1–7 = 0 | Bit 0 stored; other bits ignored; recomputes the IRQ level |
| 3 | `$D003` | — | Reserved: `0x00` | Ignored |

**STATUS reads never dequeue** (peek semantics — poll loops spin on STATUS safely).

**Bus DATA reads are destructive.** Reading DATA over the live bus dequeues the next byte — hardware-true behavior. The monitor's *display* commands (`m`, `d`, `s`) no longer take this path: they use the side-effect-free Peek API and show the queue head without consuming it. See [Monitor Reference — known behaviors](monitor-reference.md#known-behaviors).

**IRQ contract (level-shaped, matching 6502 IRQ semantics):** the UART holds its IRQ source asserted while `rx-ready && rx-irq-enable` — the line drops the moment the queue drains or the enable bit clears. The UART claims a wired-OR source handle on the machine's IRQ line during `Realize`; a bare (unrealized) UART never touches a line, so `FeedInput` stays safe in unit tests.

The host wires `OnTransmit = b => Console.Write((char)b)` — UART output prints inline to the console as the guest writes each byte.

---

## Interval timer register reference

The `IntervalTimer` is mapped at `$D100`–`$D1FF` with the same partial decode (`offset & 0x03`): the four registers mirror 64 times through the page.

| Offset (`& 0x03`) | Address | Name | Read | Write |
|---|---|---|---|---|
| 0 | `$D100` | CTRL | Live bits: bit 0 = enable, bit 1 = irq-enable, bit 2 = repeat; bits 3–7 = 0 | Bits 0–2 stored. Enable 0→1: schedule the fire PERIOD cycles from now; enable 1→0: cancel the pending fire. Irq-enable changes re-evaluate the IRQ level immediately. Repeat changes apply at the next enable or fire |
| 1 | `$D101` | PERIODL | Latched period low byte | Stored; **does not retime a pending fire** |
| 2 | `$D102` | PERIODH | Latched period high byte | Stored; same |
| 3 | `$D103` | STATUS | Bit 0 = fired; bits 1–7 = 0 | **Write-1-clear**: writing bit 0 set clears fired (and drops the IRQ level); writes without bit 0 are ignored |

**Contracts:**

- PERIOD is a 16-bit cycle count; **PERIOD == 0 means 65536** (the wrap convention — there is no dead enable state, and a guest writing 0 never faults the host).
- One-shot (repeat = 0): fires once at enable + PERIOD, sets fired, and **clears its own enable bit**.
- Repeat (bit 2 set): fires every PERIOD cycles until disabled. Clearing the repeat bit mid-flight makes the next fire the last.
- The fire lands at the **exact** cycle: the enable write's own bus cycle plus PERIOD (the scheduler binds the CPU's live cycle counter, so a `STA $D100` mid-slice schedules from real CPU time).
- IRQ is level-shaped: asserted while `fired && irq-enable`. The handler's write-1-clear to STATUS drops the level before `RTI`.
- **Every timer read is side-effect-free** — that is *why* STATUS is write-1-clear rather than read-clear. Monitor dumps over the timer page show honest values and perturb nothing.
- COUNT readback is deliberately not provided: the 4-register window is full. Recorded with timer-v2 ideas.
- **Edge worth knowing:** setting the repeat bit while a *one-shot* fire is already pending (CTRL `0x01` → `0x05` mid-flight) does not re-arm the schedule — the pending fire still lands, but with repeat now set it neither chains (one-shots schedule no chain) nor self-clears the enable bit, leaving CTRL reading enabled with nothing scheduled. Disable then re-enable to arm the repeat. ("Repeat changes apply at the next enable or fire" — in this corner the *flags* apply at the fire, not a re-arm. Recorded with timer-v2 ideas.)

---

## Reset and vector behavior

On `machine.Reset()`:

1. Vectors `$FFFC` / `$FFFD` are read to load PC. In the demo ROM both hold `$E000`.
2. S is set to `$FD`, P to `$34` (I set, bits 5 and 4 set — the power-up convention).
3. NMI pending latch is cleared.
4. The reset costs the authentic 7 cycles.

All three 6502 vectors (NMI at `$FFFA`/`$FFFB`, RESET at `$FFFC`/`$FFFD`, IRQ/BRK at `$FFFE`/`$FFFF`) are set to `$E000` in the demo ROM image.

**Vector honesty for interrupt experiments:** the ROM owns the vector table, and all vectors point at `$E000` — the demo entry. Enabling the breadboard timer's IRQ (or the UART's rx-IRQ) with the I flag clear therefore *restarts the demo*: the service jumps to `$E000` and the hello prints again. On this board, poll the timer's STATUS (`$D103`) or the UART's STATUS (`$D001`) interactively instead. For real handler experiments you need writable vectors — build a RAM-vector board with the same devices ([Building Machines](building-machines.md) shows how); the test suite's interrupt-driven UAT sessions run on exactly such a board.

---

## Demo ROM

The demo ROM is assembled at startup by the generated single-instruction assembler (`DemoRom.Build()`), which is artifact ⑤ of the spec-table pipeline — the assembler eating its own dogfood. The image is placed in a writable scratch `AddressSpace` at the same addresses, then mapped read-only in the live machine.

### Listing

The following listing was produced by `d $E000 12` against a real running instance:

```
E000: A2 00     LDX #$00
E002: BD 1E E0  LDA $E01E,X
E005: F0 07     BEQ *+7
E007: 8D 00 D0  STA $D000
E00A: E8        INX
E00B: 4C 02 E0  JMP $E002
E00E: AD 01 D0  LDA $D001
E011: 29 01     AND #$01
E013: F0 F9     BEQ *-7
E015: AD 00 D0  LDA $D000
E018: 8D 00 D0  STA $D000
E01B: 4C 0E E0  JMP $E00E
```

### What the ROM does

**Print loop (`$E000`–`$E00B`):**

1. `$E000 LDX #$00` — initialize index X = 0.
2. `$E002 LDA $E01E,X` — load the next character from the message table.
3. `$E005 BEQ *+7` — if the character is NUL (end of message), jump to the echo loop entry at `$E00E`.
4. `$E007 STA $D000` — transmit the character via UART DATA.
5. `$E00A INX` — advance the index.
6. `$E00B JMP $E002` — loop.

**Echo loop (`$E00E`–`$E01B`):**

7. `$E00E LDA $D001` — read UART STATUS.
8. `$E011 AND #$01` — test bit 0 (rx-ready).
9. `$E013 BEQ *-7` — if not ready, poll again (busy-wait; safe because tx-ready is always 1).
10. `$E015 LDA $D000` — dequeue the received byte.
11. `$E018 STA $D000` — echo it back.
12. `$E01B JMP $E00E` — repeat forever.

### Message and data layout

| Address | Content |
|---|---|
| `$E01E`–`$E039` | `"Hello from Breadboard6502!\r\n"` (28 bytes) |
| `$E03A` | NUL terminator (`$00`) |
| `$FFFA`–`$FFFB` | NMI vector: `$00 $E0` → `$E000` |
| `$FFFC`–`$FFFD` | RESET vector: `$00 $E0` → `$E000` |
| `$FFFE`–`$FFFF` | IRQ/BRK vector: `$00 $E0` → `$E000` |

### Cycle budget analysis

| Phase | Cycles |
|---|---|
| Reset sequence | 7 |
| `LDX #$00` | 2 |
| 28 × (LDA abs,X 4 + BEQ not-taken 2 + STA abs 4 + INX 2 + JMP 3) | 420 |
| Final pass (LDA abs,X 4 + BEQ taken same-page 3) | 7 |
| **Total to echo-loop entry** | **436** |

`g 1000` is always enough to see the full hello message (436 cycles needed; 1000 is ~2.3×). The `--demo` mode uses 10,000 cycles (~23× headroom).

---

## `NewMonitor()`

`Breadboard6502.NewMonitor()` returns a `MonitorEngine` wired through `Machine.Run` so that `g` and `s` tick the scheduler (required for peripherals that register events). The monitor's `i` command feeds `Uart.FeedInput`.

```csharp
public MonitorEngine NewMonitor() =>
    new(Cpu, Machine.Space(AddressSpaceKind.Program), Cpu, Machine.Run);
```

The fourth argument is the run delegate: budget-in → cycles-consumed-out. Given budget 1 it executes exactly one instruction, which keeps per-instruction step reports and trap detection exact.
