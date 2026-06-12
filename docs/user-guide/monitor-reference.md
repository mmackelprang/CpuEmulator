# Monitor Reference

The machine-language monitor provides a line-oriented REPL for interacting with the emulated machine: load and save memory, inspect and set registers, disassemble, assemble, step, and run programs. The monitor engine (`CpuEmulator.Monitor`) is CPU-agnostic; the REPL command surface is the same for every CPU.

## Argument conventions

All addresses, counts, lengths, and byte values are **hexadecimal**, with the `$` prefix optional — except where noted:

- `a $ADDR INSTR` — the `$` prefix is **required** on the address (mnemonics like `ADC` are valid bare hex; the `$` disambiguates).
- `g BUDGET` — the cycle budget is **decimal** (it compares against the CPU's decimal cycle counter).

The prompt is `* ` when running interactively. In headless/test mode (no `prompt: true`) there is no prompt character.

---

## Command table

| Syntax | Action |
|---|---|
| `m ADDR [COUNT]` | Hex-dump `COUNT` bytes starting at `ADDR`. Default count: `$40` (64 bytes). |
| `m ADDR: BB BB ...` | Write the given bytes at `ADDR`, then echo the dump of what landed. |
| `d ADDR [COUNT]` | Disassemble `COUNT` instructions starting at `ADDR`. Default count: 8. Walks via `InstructionLength`. |
| `a $ADDR INSTR` | Assemble `INSTR` at `$ADDR`; echo the encoded disassembly line; advance the assembly cursor past it. |
| `a INSTR` | Assemble at the cursor (error if no prior `a $ADDR ...` set the cursor). |
| `r` | Print the registers line. |
| `r NAME=VALUE` | Set a register by name, then print the registers line. |
| `s [N]` | Step `N` instructions (default 1). Prints a two-line step report for each. |
| `g [$ADDR] [until $TARGET] [BUDGET]` | Optionally set PC to `$ADDR`; run for `BUDGET` cycles (default 1,000,000; decimal). With `until $TARGET`: stop when `$TARGET` is reached or PC traps (per-instruction checks — `until` form only); without `until`, the run always consumes the full budget. |
| `i TEXT` | Inject the characters in `TEXT` (low byte of each) to the UART input queue. Surrounding `"…"` are stripped — useful to carry leading/trailing spaces. Nothing appended. |
| `l ADDR PATH` | Load a raw binary file at `ADDR`. `PATH` is the rest of the line and may contain spaces. |
| `w ADDR LEN PATH` | Save `LEN` bytes from `ADDR` to a raw binary file. |
| `?` | Print the command table. |
| `q` | Quit. EOF (Ctrl+Z+Enter on Windows, Ctrl+D elsewhere) also quits. Blank lines are ignored. |

---

## Output formats

All output formats are tested verbatim in the suite; they match exactly what a real run produces.

### Registers line

```
A=00 X=00 Y=00 S=FD P=34 PC=E000 CYC=7
```

Each register in `RegisterNames` order, hex at its natural width (`RegisterBits / 4` digits: 8-bit registers = 2 digits, 16-bit registers = 4 digits), followed by `CYC=` and the decimal cycle count.

For the 6502: `A`, `X`, `Y`, `S`, `P` = 2 hex digits; `PC` = 4 hex digits.

### Disassembly line

```
0202: 8D 00 D0  STA $D000
```

Format: `{addr:X4}: {bytes,-8}  {text}` — bytes are hex, space-separated, left-aligned in an 8-character field, two spaces, then the disassembly text.

Examples from a real run:

```
E000: A2 00     LDX #$00
E002: BD 1E E0  LDA $E01E,X
E005: F0 07     BEQ *+7
E00E: AD 01 D0  LDA $D001
E013: F0 F9     BEQ *-7
```

Undefined opcodes disassemble as `???` and have length 1 so a walk always advances.

### Hex-dump line

```
0000: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 |................|
```

Format: `{addr:X4}: {hex,-47} |{ascii}|` — 16 bytes per line, hex pairs space-separated padded to 47 characters, pipe-delimited ASCII column (printable `0x20`–`0x7E`, else `.`).

A partial last line example:

```
0300: 41 42 43                                        |ABC|
```

### Step report (two lines)

```
E000: LDX #$00
A=00 X=00 Y=00 S=FD P=36 PC=E002 CYC=9
```

Line 1: `{pc:X4}: {disassembly}`. Line 2: the registers line after the step.

When an interrupt is serviced instead of an instruction, line 1 reads:

```
{pc:X4}: (interrupt serviced)
```

followed by the registers line after the service: the 7-cycle sequence pushes PC and P (S drops by 3), sets the I flag, and loads PC from the interrupt vector's contents. The `InterruptPending` flag is sampled before the step; when true, the step report says `(interrupt serviced)` regardless of what instruction sits at PC. (The host REPL has no command to assert interrupt lines — lines are host/peripheral domain — so this report appears in embedded-monitor scenarios; it is pinned by the `MonitorEngineExecutionTests`/`MonitorReplTests` suite.)

### `g` stop lines

```
target $0205 reached after 6 cycles
trapped at $0200 after 3 cycles
budget exhausted at $E011 after 1000 cycles
```

- **target reached** — PC reached the `until $TARGET` address (`until` form only).
- **trapped** — PC did not advance (JMP-to-self / branch-to-self idiom detected). Trap detection runs per-instruction and exists **only in the `until` form**; a plain `g BUDGET` always consumes the full budget.
- **budget exhausted** — the cycle budget was consumed without hitting a target or trap.

All three lines above are captured from real runs.

### `l` / `w` confirmations

```
loaded $3 bytes at $0200
wrote $3 bytes from $0200
```

The byte count and address are hex.

### `i` confirmation

```
injected $2 bytes
```

The count is hex. `i` alone (no text) outputs `injected $0 bytes`.

### Error lines

Every error begins with `? `. The REPL catches guest-world exceptions (undefined opcodes, strict-bus violations) and prints them as `? message` without crashing.

---

## Command details

### `m` — memory dump / write

Dump 64 bytes:

```
* m 0300
0300: 41 42 43 00 00 00 00 00 00 00 00 00 00 00 00 00 |ABC.............|
0310: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 |................|
0320: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 |................|
0330: 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 00 |................|
```

Dump 2 bytes:

```
* m 0300 2
0300: 41 42                                           |AB|
```

Write bytes:

```
* m 0300: 41 42 43
0300: 41 42 43                                        |ABC|
```

The echo after a write shows what actually landed — over ROM, `m`-writes are silently dropped, and the echo shows the ROM's real content.

**Known behavior:** `m` over the UART DATA register (`$D000`) dequeues pending rx bytes — this is a hardware-true destructive read, not a bug.

### `d` — disassemble

```
* d E000 6
E000: A2 00     LDX #$00
E002: BD 1E E0  LDA $E01E,X
E005: F0 07     BEQ *+7
E007: 8D 00 D0  STA $D000
E00A: E8        INX
E00B: 4C 02 E0  JMP $E002
```

The walk uses `InstructionLength` to advance: undefined opcodes walk as 1 byte.

**Known behavior:** `d` reads through the live bus. Disassembling MMIO pages may trigger reads.

### `a` — assemble

The `$` prefix on the address is required:

```
* a $0200 LDA #$42
0200: A9 42     LDA #$42
* a NOP
0201: EA        NOP
* a BNE $0201
0202: D0 FD     BNE *-3
```

Branch mnemonics with a `$hhhh` absolute target are resolved by the engine: it computes `target - (address + length)` and retries with the relative offset. If the target is out of range (`-128..+127`), assembly fails.

**Known behavior:** `a` over the ROM window (`$E000–$FFFF`) reports success but no bytes land — the echo disassembles the ROM's real content.

For the assembler operand grammar (modes, width rules, error messages), see [Adding a CPU — assembler grammar](adding-a-cpu.md#assembler-operand-grammar).

### `r` — registers

```
* r
A=00 X=00 Y=00 S=FD P=34 PC=E000 CYC=7
* r PC=$0200
A=00 X=00 Y=00 S=FD P=34 PC=0200 CYC=7
* r A=42
A=42 X=00 Y=00 S=FD P=34 PC=0200 CYC=7
```

The `$` prefix is optional on the value. Register names are case-insensitive. An unknown register name prints `? unknown register 'NAME'`.

### `s` — step

Captured from a fresh boot (`s 4` steps the first four demo-ROM instructions):

```
* s 4
E000: LDX #$00
A=00 X=00 Y=00 S=FD P=36 PC=E002 CYC=9
E002: LDA $E01E,X
A=48 X=00 Y=00 S=FD P=34 PC=E005 CYC=13
E005: BEQ *+7
A=48 X=00 Y=00 S=FD P=34 PC=E007 CYC=15
HE007: STA $D000
A=48 X=00 Y=00 S=FD P=34 PC=E00A CYC=19
```

Each step costs the cycle-exact count for the instruction (the 6502 is cycle-accurate per bus transaction): LDX 2, LDA abs,X 4, BEQ not-taken 2, STA abs 4.

Note the fourth report line reads `HE007: STA $D000` — the `H` is the byte the guest transmitted while `STA $D000` executed, printed inline by the raw UART passthrough before the report line (the report prints after the instruction runs). This interleaving is normal; see [known behaviors](#known-behaviors).

### `g` — run

Run to the default budget:

```
* g
budget exhausted at $E011 after 1000000 cycles
```

Run a bounded slice from a specific address:

```
* g $E000 1000
budget exhausted at $E011 after 1000 cycles
```

Run until a target address:

```
* g $0200 until $0205 100
Atarget $0205 reached after 6 cycles
```

Trap detection (`until` form only): if PC does not advance after an instruction, the run stops. Captured from a real session — a JMP-to-self assembled at `$0200`:

```
* a $0200 JMP $0200
0200: 4C 00 02  JMP $0200
* g $0200 until $0300 100
trapped at $0200 after 3 cycles
```

Two more `until`-form behaviors to know:

- If PC is already at the target, `g ... until` returns immediately: `target $0205 reached after 0 cycles`.
- A plain `g BUDGET` (no `until`) performs **no trap detection** — it always runs to the budget and prints `budget exhausted …`, even if the guest is parked on a JMP-to-self.

### `i` — inject UART input

```
* i HI
injected $2 bytes
* i "  hello  "
injected $9 bytes
* i
injected $0 bytes
```

Surrounding double quotes are stripped to allow leading/trailing spaces. Nothing is appended (no CR or LF). The UART rx queue is a concurrent FIFO; bytes injected while the CPU is paused wait until `g`/`s` lets the guest read them.

If no input device is wired (engine constructed without the inject delegate): `? no input device attached`.

### `l` — load file

```
* l 0200 snippet.bin
loaded $3 bytes at $0200
```

The path is the rest of the line and may include spaces; relative paths resolve against the host's working directory. Load wraps at the address space mask — loading a 64 KiB image at `$0000` fills the entire 16-bit space.

### `w` — save file

```
* w 0200 3 snippet.bin
wrote $3 bytes from $0200
```

---

## Known behaviors

### Raw UART passthrough interleaving

UART tx output is printed inline via `Console.Write` as the guest writes each byte, during `g`/`s` execution. The output appears before the stop line with no separator or newline added:

```
* g 1000
Hello from Breadboard6502!
budget exhausted at $E011 after 1000 cycles
```

```
* g 200
HIbudget exhausted at $E011 after 200 cycles
```

In the second example, `HI` was echoed by the guest's echo loop before the budget check fired.

### `i` verbatim-after-first-space

`i` dispatches everything after the first space as the text. Double-quoting: `i "HI"` and `i HI` both inject 2 bytes; `i " HI "` injects 4 bytes (the surrounding quotes are stripped but carry the inner spaces). `i` with nothing after it injects 0 bytes.

### `a`-writes over ROM are silently dropped

The non-strict bus default for ROM mappings ignores writes. `a $E000 NOP` reports success and echoes the disassembly, but the ROM byte at `$E000` is unchanged. The echo disassembles what is really there. Verify-after-write would itself require a side-effect-free read, which is recorded backlog.

### Monitor reads perturb MMIO

`m`, `d`, and `s` read through the live bus. A hex dump over the UART DATA register (`$D000`) dequeues pending rx bytes — this is a hardware-true destructive read. Dump UART STATUS (`$D001`) to poll without consuming data. A peek/poke API is recorded monitor-v2 backlog.
