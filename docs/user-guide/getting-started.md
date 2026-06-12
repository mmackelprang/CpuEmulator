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

Expected: `Passed! - Failed: 0, Passed: 848, Skipped: 0, Total: 848`.

Tests that require external test vectors (TomHarte, Klaus) skip automatically when the vectors are not present. See [Testing](testing.md) for how to fetch them.

## First session — the Breadboard6502

The `CpuEmulator.Host` project boots a pre-wired 6502 machine (52 KiB RAM, a UART at `$D000`, and an 8 KiB demo ROM at `$E000`) and drops into the machine-language monitor REPL.

### Boot to the monitor

```
dotnet run --project src/CpuEmulator.Host
```

The banner appears and the `*` prompt waits for commands:

```
CpuEmulator — Breadboard6502
6502 · RAM $0000-$CFFF · UART $D000 (DATA $D000, STATUS $D001) · ROM $E000-$FFFF (demo)
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

Save a region to a file with `w ADDR LEN PATH` and load it back with `l ADDR PATH`:

```
* w 0200 3 /tmp/snippet.bin
wrote $3 bytes from $0200
* l 0200 /tmp/snippet.bin
loaded $3 bytes at $0200
```

### Exit

Type `q` to quit. EOF (Ctrl+Z+Enter on Windows, Ctrl+D on Linux/macOS) also exits. Ctrl+C terminates the process immediately.

---

## Complete first session (full transcript)

The following transcript was captured from a real run:

```
CpuEmulator — Breadboard6502
6502 · RAM $0000-$CFFF · UART $D000 (DATA $D000, STATUS $D001) · ROM $E000-$FFFF (demo)
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

After loading, the host drops into the monitor REPL. Use `g` to run the loaded program.

---

## Known behaviors

These behaviors are deliberate and documented; they are not bugs:

- **Monitor memory commands go through the live bus.** `m $D000` reads the UART DATA register, which dequeues the next pending rx byte. If you dump DATA you consume input. Similarly, `d $D000` reads 1–3 bytes starting at DATA and consumes them. A peek/poke API that avoids side effects is backlog.
- **`a` and `m`-writes over ROM land nothing.** The ROM window is read-only. `TryAssembleAt` over `$E000` reports success but the byte is not stored — the echo shows the ROM's real content. Verified by the `Assemble_over_rom_lands_nothing_the_documented_behavior` test.
- **`i` injects verbatim — no newline appended.** `i HI` injects exactly two bytes: `H` and `I`. If the guest expects a line terminator, inject it explicitly (`i HI\r\n` injects 4 bytes). Surrounding double quotes are stripped — `i "HI"` injects the same two bytes; `i " HI "` injects four (space, H, I, space).
- **Ctrl+C kills the process.** There is no handler — if the guest hangs, Ctrl+C is the escape hatch. For bounded test runs, use `g BUDGET` with a finite cycle count (the default is 1,000,000 cycles).
- **UART output interleaves with stop lines.** The host wires the tx callback to `Console.Write` — output appears as the guest writes it, during `g`/`s`, before the stop line. This is the authentic serial-console feel; if you see `HIbudget exhausted…` the `HI` was transmitted by the guest before the budget check fired.
