# Roadmap

This page tracks what has shipped (the M1–M6 milestone arc) and what is **deferred** or a **candidate**
for future work. It is the single forward-looking index; the per-milestone detail lives in the
[architecture decision records](architecture/) (ADRs) and the [user guide](user-guide/README.md).

> **A note on prioritization.** The ordering of the deferred items below is the **owner-set priority**
> (2026-06-19) — the intended next-up sequence, not a delivery commitment. Each item is tagged
> **[deferred]** (a scoped, named follow-on the M6 arc explicitly left out) or **[candidate]** (a looser
> idea worth recording). Nothing here is scheduled.

---

## Shipped — the M1–M6 arc

| Milestone | What it delivered |
|---|---|
| **M1 — core + 6502** | `CpuEmulator.Core` contracts, the Roslyn source generator (typed C# spec → cycle-exact interpreter + disassembler + single-instruction assembler), the **MOS 6502** (151/151 documented opcodes, cycle-exact, TomHarte 1,510,000 cases + Klaus), the device layer (scheduler, interrupt lines, `SimpleUart`, `IntervalTimer`), and the CPU-agnostic monitor + REPL + host. |
| **M2 — the IL-JIT tier** | `CpuEmulator.Jit` (`JittedCpu` + `BlockCompiler`): the dual-tier, provably-equivalent execution path — PC-keyed block cache, the RAM/ROM fastmem split (MMIO bus-callout), block chaining, per-page SMC invalidation, and emitted 6502 ops including the decimal ADC/SBC arms. Validated to full parity (TomHarte through the JIT, the differential fuzzer, Klaus cycle-exact). |
| **M3 — Zilog Z80** | The framework's 2nd ISA — the full instruction set (base + CB/ED/DD/FD/DDCB/FDCB planes), the per-spec flag-bit map, register-pair aliasing, the Q/MEMPTR + undocumented X/Y bits, validated against the Z80 TomHarte corpus and ZEXALL/ZEXDOC. |
| **M4 — Motorola 68000** | The 3rd ISA — 32-bit registers over a 16-bit big-endian bus, the field-grammar decoder, the full EA-mode set, MOVE/ALU/shift/bit/BCD/control families, the CCR (X-bit), data-axis-exact against the 680x0 corpus (coarse-cycle timing by design). |
| **M5 — Intel 8086/8088** | The 4th ISA — variable-length ModR/M decode, `(CS<<4)+IP` segmentation, the full op families, the FLAGS model (AF/PF), validated against the 8088 TomHarte corpus. |
| **M6 — cross-arch JIT emit** | The "make tier-1 fast" pass (ADR 0011). Three more CPUs now emit IL for their high-ROI families, each gated on byte-identical TomHarte-through-JIT parity; the 6502 SMC/recompile-cost lever closed the W1 Klaus thrash. Plus the **test-infra speedup arc** (T1–T4: parse cache, per-worker allocation pooling, parallelized JIT sweeps, per-worker JIT reuse, and the in-tree gating policy). |

### What M6 emitted, per CPU

Each CPU's rare/exception/microcoded tail stays interpreter-fallback **by design** — the interpreter is
always the oracle and the byte-exact fallback, so partial emit is a pure performance dial.

| CPU | Emits IL for | Stays fallback (by design) |
|---|---|---|
| **6502** | the full ISA + the decimal arms; plus the **SMC/recompile-cost lever** (recompiles collapsed ~6.8× on Klaus) | BRK/RTI, undefined |
| **Z80** | LD, ALU + flags (Q/MEMPTR, X/Y), ED 16-bit (`ADC`/`SBC HL,rr`, `INC`/`DEC rr`), branch/call/stack — the Z80 JIT now **exceeds its own interpreter** on the W2 kernel | the prefix-plane long tail (block ops, ED/DD/FD/CB rarities) |
| **68000** | MOVE (the only net-new descriptor generation; needed a word-granular `Discover` fetch-stream fix), ALU + CCR (the X-bit), shifts, branch/DBcc — data-axis-exact (coarse-cycle by design) | TRAP/TRAPV/CHK/÷0/MOVEM/MUL/DIV/RTE/LINK/UNLK, address-error, privilege |
| **8086** | MOV (+ the `(CS<<4)+IP` seam), ALU + FLAGS, **near** branch/call/return | **far flow** (CS-invariant block key), MUL/DIV, string-REP, INT/INTO/IRET/BOUND, IN/OUT |

See [The JIT Tier](user-guide/jit.md) for the emit arms and the accuracy contract, and ADR 0011 for the
design rationale (the emit-vs-fallback boundary, the rollout order, the profiling-ranked ROI).

---

## Recently shipped — the "CPUs → computers" arc

| Piece | What it delivered |
|---|---|
| **#1 — the Machine model** | `CpuEmulator.Machines`, a new composition-root assembly: a declarative **`BoardSpec`** (memory map + peripheral slots + IRQ wiring + reset), a load-time **`BoardSpecValidator`** (overlap / address-width / page-alignment / MMIO-slot / IRQ-wired / ROM-size / vector-patch diagnostics), the **`CpuKind`→core factory** (interpreter + JIT tiers — the one place allowed to name both the concrete cores and the JIT, keeping `Core` AOT-clean), and **`BoardMachineFactory.Build`**, which compiles a validated spec down to the existing fluent `MachineBuilder`. The hand-wired `Breadboard6502` is re-expressed as a `BoardSpec` and proven **byte-identical (UART stream) + cycle-identical** to the original over the existing host sessions (the un-fakeable zero-behavior-change gate). A `ReferenceSbc(Z80)` reference board boots from PC=0 and prints `OK` on **both** tiers, proving the model generalizes across a genuinely different CPU + reset mechanic. The 6502 + Z80 boards ship in this piece (the 68000/8086 cores still had no-op `Reset()` stubs — the recipe deferred them to piece #2). No production file outside the new assembly was edited; the Host keeps its hand-wired board (wiring the host onto the board-spec is a later piece). |
| **#2 — 68000 + 8086 reset + reference boards** | Each CPU's `Reset()` goes from a no-op stub to **functionally-correct landed state**: the **68000** reads its initial SSP from the long at `$000000` and PC from the long at `$000004` (big-endian, via the existing wide bus) and enters supervisor mode with interrupt mask 7 and trace off (`SR=0x2700`); the **8086** jams `CS=0xFFFF, IP=0` (physical entry `0xFFFF0`), clears `DS/ES/SS`, and clears `FLAGS` (a pure register jam). The `ReferenceSbc` recipe + `CpuCoreFactory` now instantiate **both** cores on **both** tiers, placing ROM where each CPU *boots*: 68000 → ROM **low** at `$0` (carrying the `$0/$4` reset vectors + the program), RAM high, on a **big-endian** 24-bit bus (a new per-space `Endianness` seam threads the byte order from `BoardSpec` through `BoardMachineFactory`); 8086 → ROM **high** `0xF0000–0xFFFFF` (covering `0xFFFF0`), RAM low, on a 20-bit bus. Each board boots its ROM and runs a tiny hand-assembled program to a verifiable **`OK\r`** UART result on **both** tiers — the un-fakeable smoke (the 8086 uses the real-PC **far-JMP-at-the-entry** idiom, since the 21-byte body can't fit in the 16 bytes below the top of memory). Reset is **not** cycle-gated (no TomHarte reset vectors exist); functionally-correct landed state is the bar. |
| **#3 — the monitor hosts** | The console host boots **any** board into the CPU-agnostic monitor/REPL via `--board <name>` (default `6502`; `--board list` enumerates). A `BoardRegistry` (in `CpuEmulator.Host`) maps names → a built `Machine` (through `BoardMachineFactory`, no more hand-wiring) + the `SimpleUart` the host bridges console stdin/stdout through. The 6502 path runs the `Breadboard6502`-as-`BoardSpec` (byte-identical to the retired hand-wired board); the Z80/68000/8086 paths run the piece-#2 `OK\r` boot ROMs. Each board's **host smoke** proves boot → right per-CPU registers + (real) disassembly → step/run → UART round-trip on the interpreter (Z80 also on the JIT). The hand-wired `Breadboard6502` host class is retired (the design's no-separate-path non-goal). One monitor generalization shipped: the `a`-command absolute-target parser is now address-width-aware (was 16-bit-only), so branch-offset resolution works on the 24/20-bit boards. |
| **SP0 — the web-surface foundation** | The reusable, GUI-free **web surface** for the "real machines" arc. Three additive `Core` contracts — **`IDisplayDevice`** (host pulls RGBA; the chip does palette/mode lookup so the surface is a dumb blitter), **`IKeyboardSink`** + portable **`KeyEvent`/`KeyCode`** (host pushes; the chip owns the native scan mapping), **`IBlockDevice`** (raw sector storage; controllers + image formats are SP1+). Three generic demo devices in `CpuEmulator.Peripherals` (`DemoFramebuffer` 256×192 8bpp palettized, `DemoKeyboard` UART-rx-shaped with level-IRQ, `DemoDisk` over a raw `DiskImage`). A new **`CpuEmulator.Surface.Web`** project: an ASP.NET Core minimal HTTP+WebSocket server (built into .NET 10 — no heavy dependency) → a browser HTML/JS **canvas** client (binary RGBA frames out, JSON key events in), plus the **`MachineHost`** pump (wall-clock-paced or headless/fast). The demo is a declarative **`DemoBoard` `BoardSpec`** built via `BoardMachineFactory` — a parallel surface to the monitor host over the same `Machine`. The gate is the **un-fakeable headless acceptance test** (no browser, no throttle): the demo ROM paints a gradient test pattern (display out), echoes a synthetic `PostKey` into VRAM (input round-trip), and reads disk sector 0 onto the screen (block device) — all asserted on the real RGBA / VRAM / disk bytes. |

---

## Deferred & candidate follow-ons

These were surfaced and explicitly scoped-out during the M6 arc, in **owner-set priority order**
(2026-06-19) — the intended next-up sequence, not a schedule.

1. **[deferred] 8086 far-flow emit.** Far `JMP`/`CALL`/`RET` (and far interrupts) stay fallback because
   the block-cache key is `(IP)`, CS-invariant. Emitting them requires **widening the cache key to
   `(CS,IP)`** so a far transfer to the same offset under a different segment is a distinct block. The
   most-named M6 gap — it unblocks real-mode 8086 programs.

2. **[deferred] Cycle-exact emitted 68000 timing (the prefetch-queue model).** The 68000 is
   data-axis-exact but charges **coarse cycles** today; the cycle-exact axis (ADR 0008 §6 / ADR 0011 OQ4)
   — the prefetch-queue model — would make the emitted 68000 cycles/sec trustworthy and let it report
   cycles instead of leading with guest-MIPS.

3. **[deferred] 68000 bench-harness cleanups (small, bench-only).** (a) the **W3 profiler arm** — the
   hot-op profiler covers 68000 W1/W2 but not W3; (b) the **W2 cycle off-by-2** — a small cycle discrepancy
   in the 68000 W2 bench harness (affects the bench number, not interpreter/JIT parity). *(Both tracked
   backlog.)*

4. **[deferred] 8086 MUL/DIV + string/REP + INT/IRET emit.** The microcoded multiply/divide, the
   `REP MOVS/STOS/CMPS/SCAS` CX-counted string loops, and the INT/IRET vectoring machinery are fallback by
   design (rare, high-emit-cost). A future profile could justify emitting any of them.

5. **[candidate] Per-bank specialization + the generic emitter.** (a) **Per-bank `(PC, bankState)` block
   specialization** (ADR 0009 OQ3) — key blocks on `(PC, bankState)` so a re-entered memory bank reuses
   compiled blocks instead of evicting on every bank switch (complementary to the M6 SMC lever, which
   handles self-modifying code, not bank-switching). (b) the **generic `OpModel`-walked emitter** (ADR
   0011 Decision 2 / OQ2) — promote the hand-written per-CPU emit arms to a single spec-`OpModel`-driven
   emitter (with per-CPU flag/cycle plug-ins) once ≥2 CPUs' arms reveal what genuinely generalizes; the
   descriptor `Ops` bridge keeps this possible with no spec change.

6. **[candidate] A real 68000 disassembler.** The monitor renders `???` for 68000 instructions —
   the field-grammar 68000 has no flat per-opcode disassembly table (the generated `Disassemble`
   is a stub). A field-grammar-walking disassembler would give the 68000 monitor host the same
   mnemonic rendering the 6502/Z80/8086 already have. Surfaced by "CPUs → computers" piece #3.

7. **[deferred] `IAudioSink` for the first real machine's beeper.** SP0 deliberately omits sound. The
   first real machine (e.g. the ZX Spectrum 48K beeper) needs a host-facing audio-output contract,
   shaped like the SP0 display/keyboard contracts (the chip produces samples; the surface plays them
   over the WebSocket). Designed at that machine's spec time, not built in SP0.

**Further candidates (unprioritized):**

- **[investigated → refuted + shelved] Per-dispatch JIT-overhead (#42).** Hypothesis (from #40): the
  `InvalidateIfDirty` 256-page scan was the SMC-heavy per-dispatch floor (the 6502 Klaus JIT runs ~140×
  slower than the interpreter even with PR-S engaged). **Measurement refuted it** — the scan is ~1.3% of
  runtime, not the floor (only 2,709 evictions across 161,805 invalidate calls on Klaus); the dirtied-page-list
  rewrite was byte-identical-correct but a *net-negative*. The real ~140× floor is the dispatcher round-trip
  + chaining/`ResolveChain` per-edge cost + `Evict`'s dictionary churn. **Shelved** — the two-tier design
  already covers SMC-heavy / integration code (run it on the interpreter tier; the JIT earns its keep on the
  hot compute kernels, where it's 1.2–3.1×). See
  [ADR 0012](architecture/0012-jit-dirty-page-list-invalidation.md) (Rejected).
- **[candidate] Z80 / 68000 tail emit** (PR-2b-style — emit selected hot prefix-plane / microcoded
  members as profiles dictate; the Z80 ED 16-bit ops showed this is cheap when a tail op recurs).
- **[candidate] A cycle-exact 8086 timing model** (M5 charges a rudimentary one-cycle-per-bus-access
  model today).

---

## Pointers

- Per-milestone design: [`docs/architecture/`](architecture/) (ADRs 0001–0011).
- The M6 emit design + PR arc: ADR 0011 (`docs/architecture/0011-jit-hot-op-emission-optimization.md`).
- The JIT tier (emit arms, accuracy contract, chaining, the SMC lever):
  [`docs/user-guide/jit.md`](user-guide/jit.md).
- Benchmarks + the before/after speedup story: [`docs/user-guide/benchmarks.md`](user-guide/benchmarks.md).
- Test-suite speedup detail: [`bench/results/test-suite-speedup.md`](../bench/results/test-suite-speedup.md).
