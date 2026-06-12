# Breadboard6502

The `Breadboard6502` is the canonical pre-wired 6502 machine that `CpuEmulator.Host` boots. It composes a MOS 6502 CPU, 52 KiB of RAM, a polled UART, and an 8 KiB demo ROM assembled at startup by the generated single-instruction assembler.

Source: `src/CpuEmulator.Host/Breadboard6502.cs`, `src/CpuEmulator.Host/DemoRom.cs`

---

## Memory map

| Range | Size | Mapping | Notes |
|---|---|---|---|
| `$0000`–`$CFFF` | 52 KiB | RAM | Zero page (`$00`–`$FF`), stack (`$0100`–`$01FF`), user program space (house origin `$0200`) |
| `$D000`–`$D0FF` | 256 bytes | `SimpleUart` | DATA `$D000`, STATUS `$D001`; mirrors every 4 bytes through the page (partial decode) |
| `$D100`–`$DFFF` | ~3.75 KiB | Unmapped | Open-bus reads return `0xFF`; writes ignored |
| `$E000`–`$FFFF` | 8 KiB | ROM | Demo ROM (print loop + echo loop); reset/NMI/IRQ vectors all → `$E000` |

The board uses a 16-bit address space (`addressBits: 16`). All addresses wrap at `$FFFF`.

---

## UART register reference

The `SimpleUart` is mapped at `$D000`–`$D0FF` with partial decode: the device decodes `offset & 0x03`, so all four registers mirror 64 times through the page. `$D004` is DATA again; `$D005` is STATUS; `$D0FC` is DATA; and so on.

| Offset (`& 0x03`) | Address | Name | Read | Write |
|---|---|---|---|---|
| 0 | `$D000` | DATA | Dequeue next rx byte; **`0x00` when queue is empty** | Transmit: invoke `OnTransmit` with the low byte |
| 1 | `$D001` | STATUS | Bit 0 = rx-ready (queue non-empty); bit 1 = tx-ready (**always 1** — transmit is instantaneous in M1); bits 2–7 = 0 | Ignored |
| 2 | `$D002` | — | Reserved: `0x00` | Ignored |
| 3 | `$D003` | — | Reserved: `0x00` | Ignored |

**STATUS reads never dequeue** (peek semantics — poll loops spin on STATUS safely).

**DATA reads are destructive.** Reading DATA dequeues the next byte. A monitor `m $D000` hex dump therefore consumes pending input. This is hardware-true behavior, not a bug. See [Monitor Reference — known behaviors](monitor-reference.md#known-behaviors).

The host wires `OnTransmit = b => Console.Write((char)b)` — UART output prints inline to the console as the guest writes each byte.

---

## Reset and vector behavior

On `machine.Reset()`:

1. Vectors `$FFFC` / `$FFFD` are read to load PC. In the demo ROM both hold `$E000`.
2. S is set to `$FD`, P to `$34` (I set, bits 5 and 4 set — the power-up convention).
3. NMI pending latch is cleared.
4. The reset costs the authentic 7 cycles.

All three 6502 vectors (NMI at `$FFFA`/`$FFFB`, RESET at `$FFFC`/`$FFFD`, IRQ/BRK at `$FFFE`/`$FFFF`) are set to `$E000` in the demo ROM image. No hardware interrupt lines are asserted in M1.

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
