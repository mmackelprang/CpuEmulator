# Getting Started

## Prerequisites

- **.NET 10 SDK** — download from [dotnet.microsoft.com](https://dotnet.microsoft.com/download/dotnet/10.0). Verify with `dotnet --version` (expect `10.0.x`).
- **Git**

No other tools are required. All test-vector downloads are optional and scripted (see [Testing](testing.md)).

## Clone and build

```
git clone <repo-url>
cd CpuEmulator
dotnet build
```

Expected output: `Build succeeded. 0 Warning(s) 0 Error(s)`.

## Run the tests

```
dotnet test
```

On a fresh clone (before fetching the optional external test vectors) expect:

```
Passed! - Failed: 0, Passed: 694, Skipped: 4, Total: 698
```

The 4 skips are the vector-gated tests (TomHarte, Klaus) — they skip automatically with an actionable message when the vectors are not present. After fetching the vectors (see [Testing](testing.md)), the same command reports `Passed: 848, Skipped: 0, Total: 848` (the TomHarte theory expands to one row per opcode).

## First session — the Breadboard6502

The `CpuEmulator.Host` project boots a pre-wired 6502 machine (52 KiB RAM, a UART at `$D000`, an interval timer at `$D100`, and an 8 KiB demo ROM at `$E000`) and drops into the machine-language monitor REPL.

### Boot to the monitor

```
dotnet run --project src/CpuEmulator.Host
```

The banner appears and the `*` prompt waits for commands:

```
CpuEmulator — Breadboard6502
6502 · RAM $0000-$CFFF · UART $D000 (DATA/STATUS/CTRL) · timer $D100 · ROM $E000-$FFFF (demo)
UART output prints inline; 'i TEXT' feeds UART input; 'g' runs (reset entry $E000); '?' help; 'q' quit.
*
```

The CPU is at `$E000` (the reset vector entry point) and has not yet executed any instructions. Cycle count is 7 (the 6502 reset sequence charges 7 cycles).

### Run the demo ROM

Type `g 1000` to run for 1000 cycles. The demo ROM prints its hello message and parks in the polled echo loop:

```
* g 1000
Hello from Breadboard6502!
budget exhausted at $E011 after 1000 cycles
*
```

The message is transmitted via the UART at `$D000`; the host wires `OnTransmit` to `Console.Write`, so guest output appears inline — before the stop line, with no separator.

### Inject input and echo

The echo loop (starting at `$E00E`) polls the UART STATUS register for a received byte and echoes it. Use `i TEXT` to inject characters into the UART rx queue, then run another slice to let the guest read and echo them:

```
* i HI
injected $2 bytes
* g 200
HIbudget exhausted at $E011 after 200 cycles
*
```

The two echoed bytes (`H`, `I`) appear immediately before the budget-exhausted stop line — raw passthrough, no newline added by the host.

### Assemble and run new code

Use `a $ADDR INSTR` to assemble a single instruction at an address in RAM. The cursor advances automatically so subsequent `a INSTR` lines (without an address) continue from where the last instruction ended:

```
* a $0200 LDA #$41
0200: A9 41     LDA #$41
* a STA $D000
0202: 8D 00 D0  STA $D000
```

Run the two-instruction program from `$0200` until `$0205` (the address just past the second instruction):

```
* g $0200 until $0205 100
Atarget $0205 reached after 6 cycles
*
```

`A` is the character transmitted by `STA $D000` — printed inline before the stop line. The 6 cycles are LDA (2) + STA abs (4).

### Save and reload memory

Save a region to a file with `w ADDR LEN PATH` and load it back with `l ADDR PATH` (relative paths resolve against the directory you launched the host from):

```
* w 0200 3 snippet.bin
wrote $3 bytes from $0200
* l 0200 snippet.bin
loaded $3 bytes at $0200
```

### Exit

Type `q` to quit. EOF (Ctrl+Z+Enter on Windows, Ctrl+D on Linux/macOS) also exits. Ctrl+C terminates the process immediately.

---

## Complete first session (full transcript)

The following transcript was captured from a real run:

```
CpuEmulator — Breadboard6502
6502 · RAM $0000-$CFFF · UART $D000 (DATA/STATUS/CTRL) · timer $D100 · ROM $E000-$FFFF (demo)
UART output prints inline; 'i TEXT' feeds UART input; 'g' runs (reset entry $E000); '?' help; 'q' quit.
* g 1000
Hello from Breadboard6502!
budget exhausted at $E011 after 1000 cycles
* i HI
injected $2 bytes
* g 200
HIbudget exhausted at $E011 after 200 cycles
* a $0200 LDA #$41
0200: A9 41     LDA #$41
* a STA $D000
0202: 8D 00 D0  STA $D000
* g $0200 until $0205 100
Atarget $0205 reached after 6 cycles
* q
```

---

## The --demo mode

`--demo` resets the machine, runs it for 10,000 cycles (the hello message completes at cycle 436), prints any UART output, and exits with code 0:

```
dotnet run --project src/CpuEmulator.Host -- --demo
```

Output:

```
Hello from Breadboard6502!
```

## The --load mode

Load a raw binary file and optionally set the load address and initial PC:

```
dotnet run --project src/CpuEmulator.Host -- --load prog.bin
dotnet run --project src/CpuEmulator.Host -- --load prog.bin --at $0200 --pc $0200
```

`--at` defaults to `$0200`; `--pc` defaults to the reset vector address from ROM (`$E000`) unless explicitly set. Addresses may be given with or without a `$` prefix.

After loading, the host drops into the monitor REPL. Use `g` to run the loaded program. (Don't have a binary yet? Assemble something in a monitor session and save it with `w` — see [Save and reload memory](#save-and-reload-memory) above.)

---

## Terminal mode (--terminal)

`--terminal` opens a raw per-keystroke terminal onto the guest instead of the line-oriented monitor: every key you press becomes a byte in the UART rx queue immediately (no Enter needed), and guest UART output prints as it is transmitted. The demo ROM's echo loop makes this feel like a serial terminal session.

```
dotnet run --project src/CpuEmulator.Host -- --terminal
```

A session — boot, watch the hello, type `AB` at the echo loop, leave with Ctrl-]:

```
CpuEmulator — Breadboard6502
6502 · RAM $0000-$CFFF · UART $D000 (DATA/STATUS/CTRL) · timer $D100 · ROM $E000-$FFFF (demo)
UART output prints inline; 'i TEXT' feeds UART input; 'g' runs (reset entry $E000); '?' help; 'q' quit.
(terminal — Ctrl-] exits to the monitor)
Hello from Breadboard6502!
AB* q
```

The `AB` is the guest's echo (keystrokes are not locally echoed — what you see is what the guest transmitted). **Ctrl-]** (the telnet escape) leaves the terminal and falls through to the monitor `*` prompt — the machine state is intact, so you can inspect memory, then `q` to quit.

`--terminal --load prog.bin --pc $0300` is a legal combo: load a binary, set PC, free-run it under the terminal.

### Key mapping

| Key | Guest byte |
|---|---|
| Printable ASCII (`0x20`–`0x7E`) | the character's byte |
| Enter | `0x0D` (CR) — platform-identical (mapped by key, not by the platform's `\r`/`\n` KeyChar) |
| Backspace | `0x08` |
| Tab | `0x09` |
| Esc | `0x1B` — passes through as a byte (the exit key is Ctrl-], not Esc) |
| Ctrl+A … Ctrl+Z | `0x01`–`0x1A` — **including Ctrl+C = `0x03`, which reaches the guest** (terminal mode only) |
| **Ctrl+]** | *exit to the monitor prompt* (not guest input) |
| Arrows, F-keys (zero KeyChar) | dropped silently |

### Caveats

- **Encoding:** guest tx bytes are written as characters by identity (Latin-1 cast); your console renders them through its codepage. Printable ASCII and CR/LF are honest everywhere; bytes ≥ `0x80` render however your codepage says.
- **Interactive-only:** raw key polling needs a real console. Under redirected stdin, `--terminal` prints `? --terminal needs an interactive console: …` and exits with code 2.

---

## Known behaviors

These behaviors are deliberate, not bugs. The canonical reference (with examples) is [Monitor Reference — known behaviors](monitor-reference.md#known-behaviors); in brief:

- **Monitor display reads are side-effect-free over honest devices** — `m`/`d`/`s` peek where the device implements `TryPeek` (the UART and the timer do): `m $D000` shows the rx queue head without consuming it. Devices without an honest peek fall back to live-bus reads; `l`/`w` and guest execution always use the real bus.
- **`a` and `m`-writes over ROM land nothing** — the ROM window is read-only; the echo shows what is really there.
- **`i` injects verbatim, nothing appended** — no CR/LF is added, and there is currently no escape syntax for control bytes (recorded backlog). Quotes are stripped and carry leading/trailing spaces: `i "HI"` injects 2 bytes; `i " HI "` injects 4.
- **Ctrl+C kills the process in REPL mode** — bounded `g` budgets (default 1,000,000 cycles) are the runaway protection. **In terminal mode (`--terminal`) Ctrl+C is a guest byte** (`0x03`) instead — leave the terminal with Ctrl-] first if you want Ctrl+C to mean "kill".
- **UART output interleaves with stop lines** — guest output prints inline via raw passthrough, so `HIbudget exhausted…` means `HI` arrived before the stop line.
