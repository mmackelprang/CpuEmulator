# Roadmap

This page tracks what has shipped (the M1–M6 milestone arc) and what is **deferred** or a **candidate**
for future work. It is the single forward-looking index; the per-milestone detail lives in the
[architecture decision records](architecture/) (ADRs) and the [user guide](user-guide/README.md).

> **A note on prioritization.** The ordering of the deferred items below is the **owner-set priority**
> (2026-06-19; **the Apple ][+ planned arc PRs A–T shipped 2026-06-20/21 — structurally complete**: the base
> ][+ boot through the **dual-CPU Z-80 SoftCard CP/M boot capstone** (PR-K), the **display-multiplexer
> active-display seam** (PR-M), the **Videx Videoterm 80-column card** (PR-N), the **CP/M-on-Videx capstone**
> (PR-O — the "usable 80-column CP/M" deliverable: CP/M auto-widens to the 80-col Videx at `A>`, pending owner
> assets for the live render), and the full **surface-UI sub-arc** (rows P–T): P pushes the REAL machine
> state — board/asset/video-mode label + per-drive motor [the real `$C0E8/$C0E9` switch, false at boot] +
> image label — as a structured `ST` text frame on change (the client renders it read-only + exposes
> `window.machineStatus`); Q makes the Disk II controller hold two drive slots (drive 2 real for the first
> time) + accept runtime `Insert`/`Eject` for `.dsk`/`.po` via a shared `DiskImageFactory` (`.woz` bytes await
> a thin `WozFluxImage` follow-on — backlog row W); R adds the `GET /disks` catalog (`DiskCatalog` over
> `<cache>/disks/` + the CP/M `.dsk`) + the `disk-insert`/`disk-eject` text-WS dispatch + the drive-2 status
> fold-in (the `ST` frame now reports both drives); S adds the surface's first inbound binary path — a binary
> `DK` upload frame, reassembled across fragments + re-validated server-side, with the UPLOADING/INSERTED/error
> state machine; and **PR-T (the control-strip UI) composes P/R/S** — two bordered drive panels (library select
> + upload picker + eject + the **real-motor amber light** [`$C0E8/$C0E9` + the ~1 s 556 off-delay, never
> faked on insert] + the current-image label), the calm named-script asset banner, the read-only video-mode
> label, the one new `--drive-active` token, **plus the D5 keyboard ctrl-wiring** (a real `Ctrl+B`/`Ctrl+C`
> now folds with `$1F` to a control code end-to-end — `Ctrl+B`→`$02` at `$C000`, gated un-fakeably). **What
> remains in the Apple ][+ space: backlog row W (`WozFluxImage`, the `.woz`-file byte parser) + deferred row L
> (JIT-under-translation), plus owner-asset / owner-browser-UAT items** (fetch the Apple ROMs + CP/M `.dsk` and
> confirm the live 80-col CP/M render, the panels rendering, the amber light lingering on real disk access, and
> a real `Ctrl+B` dropping to BASIC); per-PR detail in `docs/BUILDER_QUEUE.md`) — the intended next-up
> sequence, not a delivery commitment.
>
> **CORRECTION / IN FLIGHT (2026-06-21, ADR 0017):** the PR-K/PR-O "CP/M boots to `A>` / auto-widens to 80-col"
> claim above was **over-stated** — a live UAT with the real Microsoft SoftCard CP/M 2.2 disk proved the shipped
> CP/M deliverable **never boots to `A>`**. The Architect root-caused it (ADR 0017) to a **three-defect cascade**
> (live-verified): (1) the CP/M sector skew must be **per-track** (system tracks 0–2 use a distinct boot
> interleave; the single all-tracks table loaded boot2's `$0F7D` as `$00`/BRK → a silent monitor crash);
> (2) `SoftCardControlPort.Read()` toggled the active CPU on every read, livelocking the SoftCard-detect poll →
> `CAN'T FIND Z80 SOFTCARD`; (3) `RunDualCpu` ran the active core past its `$CnXX` toggle, corrupting every BIOS
> round-trip. The fix arc is **planned (queue rows CPM-1…CPM-4)** and strictly ordered. **CPM-1 SHIPPED
> (2026-06-21):** landed defect (1) — the **per-track CP/M skew** (boot interleave `(p×11)%16` for system tracks
> 0–2, the data table for 3+, via the additive `Apple2SectorOrder.PhysicalToLogical(kind,track)` overload +
> `DskFluxImage` per-track resolution; DOS/ProDOS unregressed) so boot2's `$0F7D` is a valid opcode (no silent
> BRK-to-monitor crash) — and **de-fanged both CP/M `A>` boot gates** to an honest negative (the skew crash is
> gone) + a named-skip of the `A>` part (until CPM-4 / CPM-5), restoring a **green/honest CP/M suite** (the 2
> gates went from RED to honest-skip; the skew fix is gated by an un-fakeable regression that fails pre-fix /
> passes post-fix). **CPM-2 SHIPPED (2026-06-21, PR #131):** landed defect (2) — `SoftCardControlPort.Read()`
> is now **open-bus `0x00` with NO toggle** (only `Write()` toggles the active CPU; ADR 0017 Decision 2),
> killing the per-read toggle that livelocked the SoftCard-detect poll. Gated un-fakeably at the port level (a
> read fires 0 toggles, a write fires exactly 1 — fails pre-fix, passes post-fix); full suite green. The live
> decoded-text "`CAN'T FIND` is gone" effect is not yet observable (the detect livelock clears only with
> CPM-3's run-loop yield — live-verified byte-identical screens here), so that gate is a named-skip deferred
> to CPM-3. **CPM-3 SHIPPED (2026-06-21, PR #133):** landed defect (3) — `RunDualCpu` now drives the active
> core **one instruction at a time** via `ICpuCore.Step()` and yields the instant a `$CnXX` write requests the
> switch (ADR 0017 Decision 3), so the bus-master handoff lands at the writing instruction instead of after
> the whole slice budget; the single-CPU `RunSingleCpu` path is byte-for-byte unchanged (full suite green).
> Gated un-fakeably by a synthetic dual-CPU yield test (sentinel must not run before the Z80 — fails pre-fix,
> passes post-fix) and a live gate proving the Z80 reaches and **stably holds** real CP/M BIOS at `$Axxx`
> (pre-fix it collapsed entirely to the `$0000` reset stub). With CPM-1+2+3 the boot stably reaches the CP/M
> BIOS — **no 4th defect**, exactly as ADR 0017 predicted. **CPM-4 SHIPPED (2026-06-21) — THE HEADLINE: the
> SoftCard CP/M deliverable now actually boots to `A>`.** The live triage landed in **outcome (1)** of ADR 0017
> Decision 4's scoped hypothesis: with CPM-1+2+3 all landed, CONOUT already reaches the 40-col Apple text screen
> and the real Microsoft SoftCard CP/M 2.2 disk boots to `A>` with **no further production change** — fixes 1-3
> were the complete gating set, **no `$1010` bridge change needed**, exactly as the ADR anticipated. CPM-4 is
> therefore **test-only**: it un-skips the `A>` gate into the live un-fakeable oracle (ADR 0017 Decision 5) —
> decode the 40-col text page and assert the **decoded `A>`** (the CCP prompt) + a CP/M sign-on line (the cached
> disk signs on as `APPLE ][ CP/M 44K VER. 2.20B / (C) 1980 MICROSOFT`) + `CoprocessorActive` + a committed real
> frame hash. The decoded `A>` boot frame is captured as a human-visible PNG via `tools/BootProbe
> --cpm-screenshot`. Full Release suite green (7310 passed / 0 failed / 4 skipped), warning-clean. The "80-col
> CP/M end-to-end" headline
> is **honestly narrowed** to "CP/M boots to `A>` on the **40-col** console" + "the Videx 80×24 path proven by a
> direct render" — this CP/M master is a 40-column console (zero `$C0Bx`); an 80-col CP/M master is **owner-gated**
> (ADR 0017 Decision 6/7, PR-5 — 5 candidate masters now downloaded for the auto-engage discovery). Per-PR
> detail in `docs/BUILDER_QUEUE.md`.
>
> Each item is tagged
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
| **ZX Spectrum 48K — the first real machine** | The Spectrum as a declarative **`SpectrumBoard` `BoardSpec`** on the SP0 + Phase-1 foundations: a **Z80** + 16K ROM at `$0000` + 48K RAM at `$4000`, with one peripheral — the **ULA** on I/O port `$FE` (bit-0-clear decode across the whole 16-bit Io slot). The single `SpectrumUla` faces the guest as an `IPeripheral` and the host through three SP0 contracts at once: **`IDisplayDevice`** walks the Spectrum's non-linear screen-RAM **bit-shuffle** (`$4000`) + the `$5800` attributes + the border into a 256×192+32px-border RGBA frame at 50 Hz; **`IKeyboardSink`** maps portable `KeyCode`s onto the 8×5 key matrix read by `IN ($FE)` (A8–A15 half-row select, 0 = pressed); **`IAudioSink`** resamples the beeper (`OUT ($FE)` bit 4) to S16 PCM, and the border (`OUT ($FE)` bits 0-2) folds into the frame. The ULA raises the **50 Hz IM1 interrupt** from its scheduler tick, and binds the machine's program space in `Realize` so it reads the live RAM the guest wrote. The 16K ROM is **fetched on demand** (`tools/get-spectrum-rom.{sh,ps1}`, cached like the Klaus/ZEX assets — never vendored; ROM-dependent tests skip-with-note when absent). A **`.SNA` snapshot loader** restores the 48K registers + RAM and resumes RETN-style (popping PC off the restored stack, IFF2→IFF1). The **`SpectrumSurface`** wires the ULA as display + keyboard + audio through the Phase-1 6-arg `MachineHost`, and the web server boots the Spectrum when the ROM is cached, else the SP0 demo. The un-fakeable gates run **without the ROM**: the screen-RAM bit-shuffle render, the keyboard-matrix read, the beeper PCM (both polarities + level-carry), the border RGBA, and the `.SNA` first-frame; the ROM-boot copyright-screen gate (mostly-white paper + black text) runs on **both** tiers when the ROM is present. |
| **Apple ][+ — the second real machine (arc A–T, structurally complete)** | The Apple ][+ as a family of declarative `BoardSpec`s on the SP0/Spectrum foundations, built across 20 gated PRs (rows A–T). **The machine:** the `AddressSpace.Remap` bank-switch seam + JIT page-precise invalidation (A); the `Apple2Board` + `Apple2Iou` soft-switch decoder (B); `Apple2Video` text/lo-res/hi-res render with the verified hi-res `addr(y)` map (C); `Apple2Keyboard` (uppercase-only ][+ codes) + `Apple2Speaker` (the `$C030` toggle → S16 PCM) (D); the **Language Card** mapper, the first `Remap` consumer (E); the **Disk II** controller — the `.woz`/LSS nibble path + the `IFluxImage` track-bitstream seam (F) + the `.dsk`/`.po` re-nibblizing adapter (G); and the `Apple2Surface` + `get-apple2-roms.{sh,ps1}` ROM-boot gate (H) that reaches a live BASIC prompt and boots DOS 3.3. **(Boot now live-verified on both tiers with an owner-supplied 12 KiB Apple system ROM — an Integer-BASIC Autostart dump, so the cold boot lands at the Integer `>` prompt; a ][+ Applesoft dump would show `]` — both faithful. The H boot gate's assertion is now ROM-agnostic — a mostly-blank text screen + an ink floor that either BASIC clears — and rebuilt on the no-boot-ROM `SpecWithDiskII` board the live surface actually uses.)** **The dual-CPU SoftCard CP/M path:** the two-CPU-over-one-program-space `Machine` scaffolding (I), the `SoftCardTranslation` 6-branch table + `$CnXX` control port (J), and the interpreter-tier **CP/M boot** wiring (K). **The CP/M display:** the `DisplayMultiplexer` active-display seam (M), the **Videx Videoterm** 80-column card (N), and the **CP/M-on-Videx capstone** (O — CP/M auto-widens to the 80-col Videx at `A>`). **The surface-UI sub-arc:** the structured `ST` status frame (P), the runtime disk insert/eject (Q), the `GET /disks` library catalog + per-drive dropdown (R), the binary `DK` disk-upload path (S), and the **control-strip UI** (T) — two bordered drive panels (library select + upload + eject + a **real-motor amber light** [`$C0E8/$C0E9` + the 556 off-delay, never faked on insert] + image label), the calm named-script asset banner, the read-only video-mode label, the one new `--drive-active` token, plus the **D5 keyboard ctrl-wiring** so a real `Ctrl+B`/`Ctrl+C` folds with `$1F` to a control code end-to-end (`Ctrl+B`→`$02` at `$C000`, gated un-fakeably). Every row ships + gates on the **interpreter tier** (the oracle); ROM/CP/M/Videx assets are **fetch-on-demand, never vendored** (skip-with-note absent). **Remaining in the Apple ][+ space:** backlog row **W** (`WozFluxImage`, the `.woz`-file byte parser — F shipped the read path + seam, not the file parser) + deferred row **L** (JIT-under-translation), plus the owner-asset / owner-browser-UAT confirmations (the live 80-col CP/M render; the panels rendering, the amber light, and a real `Ctrl+B` dropping to BASIC in a browser with cached ROMs). |

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
   handles self-modifying code, not bank-switching). *The run-time bank-switch primitive this builds on —
   `AddressSpace.Remap`/`RemapPeripheral` (ADR 0009 Decision 2) + the JIT `IMapInvalidationListener` +
   `BlockCache.InvalidatePages` (page-precise eviction) + `Fastmem.Reclassify` — **shipped** as Apple ][+
   PR-A (2026-06-20): interpreter-correct on every access, JIT-page-precise on remap. What remains here is
   the (PC, bankState) keying that would reuse rather than evict.* (b) the **generic `OpModel`-walked emitter** (ADR
   0011 Decision 2 / OQ2) — promote the hand-written per-CPU emit arms to a single spec-`OpModel`-driven
   emitter (with per-CPU flag/cycle plug-ins) once ≥2 CPUs' arms reveal what genuinely generalizes; the
   descriptor `Ops` bridge keeps this possible with no spec change.

6. **[candidate] A real 68000 disassembler.** The monitor renders `???` for 68000 instructions —
   the field-grammar 68000 has no flat per-opcode disassembly table (the generated `Disassemble`
   is a stub). A field-grammar-walking disassembler would give the 68000 monitor host the same
   mnemonic rendering the 6502/Z80/8086 already have. Surfaced by "CPUs → computers" piece #3.

7. **[scheduled] `IAudioSink` + port-mapped I/O — the first-real-machine foundation.** Landed as Phase 1
   of the ZX Spectrum arc (`docs/superpowers/plans/2026-06-19-spectrum-1-extensions.md`): `IAudioSink`
   (the audio analogue of `IDisplayDevice`; the chip renders an S16 PCM frame, the surface plays it over
   the WebSocket via Web Audio), and a port-mapped peripheral slot in the board model (`BoardSpec.IoAddressBits`
   + an `Io` `PeripheralSpace` discriminator — a `BoardSpec` peripheral on the Z80 I/O port space, decoding
   the full 16-bit port address). Both proven with synthetic test devices; the ULA now consumes them — see
   the **ZX Spectrum 48K** row in *Recently shipped* (Phase 2, shipped).

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
