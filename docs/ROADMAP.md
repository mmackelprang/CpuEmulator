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
> remains in the Apple ][+ space: ~~backlog row W (`WozFluxImage`)~~ **SHIPPED (PR #140 — `.woz` parses
> end-to-end through the surface)** + deferred row L (JIT-under-translation), plus owner-asset / owner-browser-UAT items** (fetch the Apple ROMs + CP/M `.dsk` and
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
> --cpm-screenshot`. Full Release suite green (7310 passed / 0 failed / 4 skipped), warning-clean. **CPM-5
> SHIPPED (2026-06-21) — the Videx CP/M gate re-frame (ADR 0017 Decision 6) + the 80-col auto-engage question
> RESOLVED.** The Builder booted **all five** owner-downloaded candidate SoftCard CP/M masters (`cpm223-60k`,
> `ms-softcard-ii-228b`, `cpm-z80softcard`, `softcard-1980`, `premium-iie-225`) on the real SoftCard+Videx board
> with CP/M now reaching `A>`, instrumenting the Videx auto-engage signal — and **none auto-hunts/auto-engages
> the Videx** (zero `$C0Bx`; three crash in 6502 boot2 on a skew mismatch, two never hand off to the Z80). So the
> "80-col CP/M end-to-end" headline is **honestly settled** as "CP/M boots to `A>` on the **40-col** console" +
> "the Videx 80×24 path proven by an asset-free direct render": the re-framed Videx gate asserts the cached 40-col
> master boots to `A>` with `ActiveIndex==0` (the multiplexer correctly stays Apple-40 — the hardware truth; the
> production auto-switch wiring is live in the gate), and the Videx 80×24 render is proven independently by the
> PR-N `VidexVideotermTests` direct-render gates. **A true 80-col (Videx-console) CP/M master remains an
> owner-asset item** (Decision 7); if one is sourced, the gate gains a sibling asserting `ActiveIndex==1`. Full
> Release suite green (7311 passed / 0 failed / 3 skipped), warning-clean. **The SoftCard CP/M arc is complete
> (CPM-1…CPM-5 ✅).** Per-PR detail in `docs/BUILDER_QUEUE.md`.
>
> **IN FLIGHT (2026-06-22, ADR 0018) — apl2cpm3 CP/M 3.1 in 80 columns on the Videx (the real 80-col CP/M console
> the 2.2 arc could not produce).** A live instruction-step trace of the real **apl2cpm3 / CPM3.1_Z80_Softcard**
> Disk 1 (CP/M 3.1, 46K TPA, no banked memory; README pins SoftCard in **slot 4**, 80-col card in slot 3) on the
> shipped `SoftCardBoard` pins the gating blocker: apl2cpm3 hard-codes `STA $C400` (slot 4) to start the Z80, but our
> board decodes the control port at `$C500` (slot 5) — so the writes hit an empty MMIO hole and the boot prints
> `NO Z80 FOUND`. At **slot 4** the Z80 activates and the CP/M-3 loader (CPMLDR / `CP/M V3.0`) loads to RAM `$1100+`,
> no crash. The fix is one additive, defaulted **per-board control-port-slot parameter** (the 2.2 board stays slot 5,
> byte-for-byte unchanged); everything else is reused unchanged (dual-CPU model, write-only toggle + per-instruction
> yield, the per-track `SectorOrderKind.Cpm` skew — live-verified correct for apl2cpm3, the 64K Language Card —
> live-confirmed wired, and the Videx slot-3 CRTC + `DisplayMultiplexer` auto-switch — already wired, waiting on the
> boot). One residual (the Z80 NOP-slides from `$0000` because its entry vector to the loaded loader is absent) is a
> bounded Builder bring-up against the live disk, the same shape that closed 2.2's `$1010` bridge. apl2cpm3 is the
> first real CP/M expected to engage the Videx 80-col path (`ActiveIndex==1`) — the owner-sourced 80-col master ADR
> 0017 OQ2 left open. **PR sequence (PLANNED 2026-06-22 — queue rows V80-1…V80-3, strictly ordered):** **V80-1**
> the configurable per-board SoftCard slot (`controlPortBase`; default slot 5 keeps the 2.2 board byte-for-byte
> unchanged, apl2cpm3 → slot 4) + the `Apl2Cpm3` asset loader + an honest skipped gate → **V80-2** CP/M 3.1 `A>`
> in 40-col (close the Z80-entry handoff via live triage) → **V80-3** CP/M 3.1 `A>` in **80 columns on the Videx**
> (`ActiveIndex==1` from a real boot — the headline). See **ADR 0018** + `docs/BUILDER_QUEUE.md`.
> **V80-1 SHIPPED (2026-06-22, PR #137):** the per-board `controlPortBase` slot (default slot 5, the 2.2 board
> byte-for-byte unchanged; apl2cpm3 → slot 4) + the `Apl2Cpm3` loader (distinct `cpm/apl2cpm3/` cache path) +
> `tools/get-apl2cpm3.{sh,ps1}` + the named-skipped boot gate, with the un-fakeable slot-placement gate
> ($C400 toggles the slot-4 board, $C500 does not). **Live-verified on the real apl2cpm3 Disk 1:** on the
> slot-4 board the Z80 activates (no error); on the slot-5 default board the boot prints `NO Z80 FOUND` — the
> gating fix ADR 0018 traced. V80-1 does not yet reach `A>` (the Z80-entry handoff is V80-2). The 2.2
> no-regression gates (CPM-4 hash, CPM-5 Videx) ran live and passed. V80-1 does not yet reach `A>` (V80-2).
> **V80-2 ROOT-CAUSED + UNBLOCKED (2026-06-22, ADR 0018-A).** The V80-2 Builder hit a NOP-slide and escalated on
> the flagged Z80-reset risk. The Architect mined apl2cpm3's **own boot source** (`BOOTLDR.MAC` et al. on Disks
> 5–6) + a RAM-correlation trace and overturned the framing: the Z80 cold-start (a deliberate NOP-slide from
> `$0000` onto `CPMLDR.COM` at Z80 `$0100`) is **faithful and already works** — **there is NO Z80-core change**
> (ADR 0018 Decision 6 / OQ1 resolved in the negative). The real blocker is a **double sector-skew** in the 6502
> boot-read path: apl2cpm3's `BOOTLDR` reads `CPMLDR.COM` through the Disk II interface ROM with its own software
> `xlt` skew, which composes with our `DskFluxImage` `CpmBootPhysToLog` pre-skew, page-permuting the load so a
> `JP (HL)` (`$E9`) lands at Z80 `$0100` instead of `LD SP` (`$31`). Proven: the composition reproduces the
> measured permutation 15/15 on track 0. The fix is an **additive, apl2cpm3-scoped disk-skew correction** (a new
> `SectorOrderKind.Cpm3` table, or raw/identity boot-track presentation) on the existing per-board
> `DskFluxImage(disk, kind)` seam — the shared 2.2 path and the Z80 core are byte-for-byte untouched. See
> **[ADR 0018-A](architecture/0018-A-apl2cpm3-z80-coldstart-and-the-bootldr-software-skew.md)** + the re-pointed
> V80-2 plan. **V80-2 unblocked (⛔→📋); V80-3 unchanged behind it.**
>
> **V80-2/V80-3 PARTIAL — the CP/M-3 console renders on the Videx; `A>` blocked by a fifth layer (2026-06-22).**
> The combined V80-2+V80-3 work landed the in-scope fix and live-triaged the boot to its true ceiling. **Shipped
> + green:** (1) `SectorOrderKind.Cpm3` = **raw DOS 3.3 on every track** (ADR 0018-A live-resolved: BOOTLDR's
> `xlt` + the running LDRBIOS `fdxlt` compose to identity over a raw presentation, so the disk is laid down
> un-skewed — NOT the per-track split the plan guessed; under it CPMLDR's `LD SP` (`$31`) lands at Z80 `$0100`
> byte-exact); (2) the **`?jsr65` Z80↔6502 service-loop bridge round-trips with NO change** — the live boot shows
> ~73 hand-backs (ADR 0018-B's predicted dead bridge is **falsified**; the natural `$03C9` resume already re-enters
> the `L65A` loop); (3) with the **REAL Videx firmware** (`videx-firmware.rom`, cached) the apl2cpm3 CRT80 console
> works: `?icrt` programs the Videx CRTC for 80×24 and `?odcrt` paints the **genuine CP/M 3.1 sign-on**
> (`CP/M Version 3.0, 56K BIOS R6/89` / `46K TPA`) into the Videx `$CC00` VRAM — decoded off the live VRAM by the
> `[Apl2Cpm3VidexFact]` gate (`Cpm3_boots_to_the_A_prompt_in_80col_on_the_Videx_interpreter`) + the
> `tools/BootProbe --apl2cpm3-videx` screenshot. (With the **synthetic** all-zero firmware the prior pass saw
> nothing — the real firmware is the load-bearing console unblock.) **The wall (escalated, NOT faked):** the boot
> renders the sign-on but does **not** reach `A>`. After the sign-on the CCP takes control (Z80 `JP $0100`,
> `CALL 5`) and the **banked CP/M-3 BDOS** path hits a **deterministic** execution divergence — a conditional
> `RET` returns to a zeroed region (Z80 `$1901`) and the Z80 NOP-slides (reproduced byte-identically: instr 36583,
> `PC=$1929`). This is a **fifth layer** in the banked BDOS/CCP execution (the Z80 core / SoftCard translation /
> Language-Card-banking model), which **ADR 0018-A A1 + the V80-2 hard constraints put off-limits** for this PR
> (no Z80-core, no translation change). So `A>` and `ActiveIndex==1` are **not yet achieved**; the headline
> ("80-col CP/M end-to-end") **remains open**, now pinned to a concrete, reproducible BDOS-execution divergence for
> the owner to scope. The 2.2 no-regression gates (CPM-4 hash, CPM-5 Videx `ActiveIndex==0`) ran live and passed.
>
> **🎉 V80-2/V80-3/V80-4 SHIPPED — CP/M 3.1 BOOTS TO `A>` IN 80 COLUMNS ON THE VIDEX (2026-06-22, PR #139).** The
> fifth-layer wall is down. **[ADR 0018-C](architecture/0018-C-apl2cpm3-language-card-bank2-write-enable-flip-flop.md)**
> root-caused the BDOS divergence to the **Language-Card write-enable flip-flop** conflating the real 74LS175's TWO
> latches (MAME `ramcard16k` `do_io` / Sather ch.5) and clearing write-enable on an odd-address WRITE — so apl2cpm3's
> `?ldccp` bank-2-select write (`LD ($C08B),A`) write-protected LC bank 2 and the `LDIR` CCP copy was silently
> dropped (bank 2 zeroed → `RET` into a zeroed `$1901`). **Classified SAFE** (the fix makes the LC model MORE
> faithful to documented hardware — no Z80-core / translation / handoff / skew / 2.2-board change) and **V80-4**
> productionized the one-method two-latch correction in `Apple2LanguageCard.Access`: even access clears both latches;
> an odd-address write clears only the pre-write count (write-enable survives); two odd reads enable. With it, LC
> bank 2 receives the CCP (3026/4096 bytes) and the real apl2cpm3 Disk 1 reaches the decoded **`A>`** on the Videx
> 80-col VRAM. **V80-3** closed the auto-engage: the live pin showed apl2cpm3 programs the Videx CRTC (`$C0B1`: 420
> writes) but never bank-selects, so `VidexVideoterm` now also engages on a CRTC-data write — the
> `DisplayMultiplexer` flips to `ActiveIndex==1` from a real CP/M-3 boot (the 2.2 master issues zero `$C0Bx` so its
> CPM-5 `ActiveIndex==0` gate is unchanged — the disk-driven contrast). **Gates:** `[Apl2Cpm3VidexFact]` strengthened
> to the decoded `A>` + a LC-bank-2-nonzero discriminator + a positive LC two-latch unit test; the 2.2 CPM-4/CPM-5
> gates + all 13 LC unit tests + the full Apple2/SoftCard/dual-CPU sweep stay green live. Screenshot
> `/d/prj/cpm-videx-80col-A-LIVE.png` (frame SHA-256 `627a1657…5004`). **This is the first true 80-column CP/M
> end-to-end — the headline the M6 / ADR 0016/0017 arc set out to reach. ADR 0017 OQ2/D6 (`ActiveIndex==1` from a
> real CP/M boot) is CLOSED.**
>
> **PLANNED (2026-06-22, Planner) — three follow-ons now have bite-sized TDD plans + queue rows
> (`docs/BUILDER_QUEUE.md` rows W / D68 / B68-DOC), priority W → D68 → B68-DOC, all independent:** the
> **`WozFluxImage` `.woz`-file parser** (Apple ][+ backlog row W — the WOZ2 container → `IFluxImage`, wired
> end-to-end through the surface's insert/upload/library) — **✅ SHIPPED 2026-06-22 (PR #140): WOZ2 INFO/TMAP/TRKS
> → `IFluxImage` bitstreams the live `Apple2DiskII` head reads, CRC32-verified, wired through
> `DiskImageFactory`/upload/catalog; asset-free round-trip/CRC32/TMAP/rejection gates green, the asset-gated
> headline gate skip-with-note (no public-domain WOZ2 located — W-8 fallback)**; a **real 68000 disassembler** (deferred #6 below —
> a `FieldGrammar`-walking disassembler so the `--board m68000` monitor renders mnemonics, monitor-display-only,
> no IL); and the **bench doc-reconciliation** (deferred #3 below — doc-only: the W3 profiler arm shipped in
> `bc68ee7`, the W2 off-by-2 is accepted coarse-cycle slack per DECISION T2). Specs:
> `docs/superpowers/specs/2026-06-22-{woz-flux-image,m68000-disassembler,bench-doc-reconciliation}-design.md`.
> (The **[deferred]/[candidate]** tags on items #3 and #6 are updated to **[resolved]** / planned by their
> Builder PRs.)
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
| **Apple ][+ — the second real machine (arc A–T, structurally complete)** | The Apple ][+ as a family of declarative `BoardSpec`s on the SP0/Spectrum foundations, built across 20 gated PRs (rows A–T). **The machine:** the `AddressSpace.Remap` bank-switch seam + JIT page-precise invalidation (A); the `Apple2Board` + `Apple2Iou` soft-switch decoder (B); `Apple2Video` text/lo-res/hi-res render with the verified hi-res `addr(y)` map (C); `Apple2Keyboard` (uppercase-only ][+ codes) + `Apple2Speaker` (the `$C030` toggle → S16 PCM) (D); the **Language Card** mapper, the first `Remap` consumer (E); the **Disk II** controller — the `.woz`/LSS nibble path + the `IFluxImage` track-bitstream seam (F) + the `.dsk`/`.po` re-nibblizing adapter (G); and the `Apple2Surface` + `get-apple2-roms.{sh,ps1}` ROM-boot gate (H) that reaches a live BASIC prompt and boots DOS 3.3. **(Boot now live-verified on both tiers with an owner-supplied 12 KiB Apple system ROM — an Integer-BASIC Autostart dump, so the cold boot lands at the Integer `>` prompt; a ][+ Applesoft dump would show `]` — both faithful. The H boot gate's assertion is now ROM-agnostic — a mostly-blank text screen + an ink floor that either BASIC clears — and rebuilt on the no-boot-ROM `SpecWithDiskII` board the live surface actually uses.)** **The dual-CPU SoftCard CP/M path:** the two-CPU-over-one-program-space `Machine` scaffolding (I), the `SoftCardTranslation` 6-branch table + `$CnXX` control port (J), and the interpreter-tier **CP/M boot** wiring (K). **The CP/M display:** the `DisplayMultiplexer` active-display seam (M), the **Videx Videoterm** 80-column card (N), and the **CP/M-on-Videx capstone** (O — CP/M auto-widens to the 80-col Videx at `A>`). **The surface-UI sub-arc:** the structured `ST` status frame (P), the runtime disk insert/eject (Q), the `GET /disks` library catalog + per-drive dropdown (R), the binary `DK` disk-upload path (S), and the **control-strip UI** (T) — two bordered drive panels (library select + upload + eject + a **real-motor amber light** [`$C0E8/$C0E9` + the 556 off-delay, never faked on insert] + image label), the calm named-script asset banner, the read-only video-mode label, the one new `--drive-active` token, plus the **D5 keyboard ctrl-wiring** so a real `Ctrl+B`/`Ctrl+C` folds with `$1F` to a control code end-to-end (`Ctrl+B`→`$02` at `$C000`, gated un-fakeably). Every row ships + gates on the **interpreter tier** (the oracle); ROM/CP/M/Videx assets are **fetch-on-demand, never vendored** (skip-with-note absent). **Remaining in the Apple ][+ space:** ~~backlog row **W** (`WozFluxImage`)~~ **W SHIPPED (PR #140 — the WOZ2 `.woz`-file byte parser → `IFluxImage`, wired through insert/upload/library; F shipped the read path + seam, W added the file parser)** + deferred row **L** (JIT-under-translation), plus the owner-asset / owner-browser-UAT confirmations (the live 80-col CP/M render; the panels rendering, the amber light, and a real `Ctrl+B` dropping to BASIC in a browser with cached ROMs). |

---

## Deferred & candidate follow-ons

These were surfaced and explicitly scoped-out during the M6 arc, in **owner-set priority order**
(2026-06-19) — the intended next-up sequence, not a schedule.

1. **[planned] 8086 far-flow emit.** Far `JMP`/`CALL`/`RET` (and far interrupts) stay fallback because
   the block-cache key is `(IP)`, CS-invariant. Emitting them requires **widening the cache key to
   `(CS,IP)`** so a far transfer to the same offset under a different segment is a distinct block. The
   most-named M6 gap — it unblocks real-mode 8086 programs. **Designed in [ADR 0019](architecture/0019-8086-far-flow-emit-and-the-cs-ip-block-key.md)**
   (Proposed, 2026-06-22): widen the shared block-cache key to the **generic 32-bit linear `(CS<<4)+IP`**
   (the physical the decode/fetch already compute), projected per-CPU via `IJitTarget.ProjectBlockKey` — the
   non-segmented 6502/Z80/68000 project the identity `(uint)PC`, so they are **byte-for-byte unchanged**
   (classified **SAFE**, gated by a key-projection identity regression). Emit far `JMP`/`CALL`/`RET`;
   `INT`/`INTO`/`IRET`/`BOUND` stay fallback (ADR 0011 §2/OQ5). A short arc — **FF-1** (the linear key + the
   SAFE identity gate) **→ FF-2** (the far arms + the un-fakeable aliasing regression: two segments, same
   offset, distinct blocks — fails on the old `(IP)` key, passes on the linear key). **Planner-decomposed
   2026-06-22 into queue rows FF-1 → FF-2** (`docs/BUILDER_QUEUE.md`; plans under `docs/superpowers/plans/2026-06-22-ff{1,2}-*.md`)
   — **strictly ordered, FF-2 not co-merged with FF-1**. Builder clears them after W → D68 → B68-DOC.

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
