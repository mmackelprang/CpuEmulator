# Builder Queue

> **Last updated:** 2026-06-21 (Planner — **CP/M-display batch N + O PLANNED** (the "usable 80-column CP/M" deliverable, ADR 0016): the Videx 80-col card + the CP/M-on-Videx capstone now have detailed bite-sized plans, grounded against `main` @ `59c1c05` (PRs #99–#114). **N** ([plan](superpowers/plans/2026-06-20-apple2-pr-n-videx-videoterm.md), deps A,M ✅): `VidexVideoterm : IPeripheral, IDisplayDevice` — the 6845 CRTC at `$C0B0`/`$C0B1`, 2 KiB VRAM as 4×512-byte banks into `$CC00–$CDFF`, the `$C800` firmware window, an 80×24 RGBA render through a **synthetic** char ROM (`VidexFont`), the `$C800` mapper as the **2nd `AddressSpace.Remap` consumer** (after the LC), and `ActiveChanged` — the guest-driven active-display signal wired to the shipped `DisplayMultiplexer.SetActive` (PR-M). Gate (asset-free, always-runs): the CRTC init → 80×24, VRAM+synthetic-charROM → inked 80×24 RGBA, the bank `Remap`, and a `DisplayMultiplexer` over `[apple40, videx80]` switches on the signal. The IOU delegates `$C0Bx` (the `$C08x`→LC / `$C0Ex`→Disk II pattern); `Apple2Board.SpecWithVidex` carves the `$C800` band validator-clean. **O** ([plan](superpowers/plans/2026-06-20-apple2-pr-o-cpm-on-videx.md), deps K,N): `SoftCardVidexBoard` (SoftCard coprocessor + `$C500` control port + the Videx `$C800` window in one validator-clean band) + `SoftCardVidexSurface` (the `DisplayMultiplexer([apple40,videx80])` whose active source follows the Videx's `ActiveChanged` — the guest-driven auto-switch, no UI toggle) + `VidexRom` + `get-videx-roms.{sh,ps1}` (optional Videx ROMs; synthetic fallback). Gate: the real `$C600`→tracks→`$CnXX` boot hands off to the Z80, CP/M's terminal driver enables the Videx → the multiplexer switches → `A>` paints on the **Videx 80-col render** (`ActiveIndex==1` + structural ink + `CoprocessorActive`) — **interpreter-tier** (the row's "both tiers" is imprecise; the coprocessor has no JIT path — ADR 0015 D4), **asset-gated/skip-with-note**. **Two shipped-API drifts flagged in the plans + carried below:** (1) **`IFastMemoryProvider` is NOT in `src/`** (ADR-0009-designed, never shipped — like `TimingTier`); N drops it and realizes the fast-RAM intent through the shipped `Remap`-to-RAM seam (VRAM = plain writable RAM behind the `$C800` window). (2) **the queue row O's "both tiers" is imprecise** — the CP/M/Z80 path is interpreter-only. **Both N + O are Builder-ready** — N (deps A,M ✅) is the topmost eligible row; O follows N. **Next: Builder picks up N.**). (Builder — **K SHIPPED (PR #113)**: the dual-CPU **capstone** — the SoftCard board composes the shipped seams (`Apple2Board.SpecWithSystem` + `WithCoprocessor(Z80)`/`CoprocessorSpec` + `SoftCardTranslation` + `SoftCardControlPort` at `$C500`), the CP/M data-track skew (`SectorOrderKind.Cpm`, research §5) lands in the shipped `.dsk` adapter, plus `SoftCardCpm` (the 143,360-byte loader), `SoftCardSurface`, and `get-softcard-cpm.{sh,ps1}` (Asimov mirror, never vendored). Full suite **7203 passed / 0 failed / 5 skipped**, warning-clean. The headline CP/M-to-`A>` boot gate is **asset-gated, SKIPPED** (no `.dsk`/system ROM cached here) — a skipped gate is GREEN; the **live CP/M-to-`A>` confirmation is pending the fetched asset** (owner runs `get-softcard-cpm` + `get-apple2-roms`). Web-surface UAT (no browser MCP; HTTP+WS frame level): server boots, `GET /`+`/app.js` 200, WS 101, `ST` text frame leads, `FB` frames stream 256×192, zero server errors — the modified `Program.cs` did not regress the Apple/Spectrum/demo fall-through. Pre-merge review: 1 MEDIUM (placeholder fetch URL — by-design per the plan; mitigated with a placeholder-detection guard) + 1 LOW (lazy `.dsk` probe) both fixed. **M SHIPPED (PR #114)**: the ADR 0016 Decision 1 active-display seam — `DisplayMultiplexer : IDisplayDevice` (Core) delegates `Width`/`Height`/`RenderInto`/`FrameReady` to the active of N sources, `SetActive(int)` fires `FrameReady` on an actual switch (so the surface re-pulls + re-sizes), a dormant source's frames are dropped, single-source is transparent; + the one `MachineHost` change — drop `readonly` on `_rgba`, `EnsureFrameBuffer()` re-allocs to the active source's geometry only when it changed (a strict no-op for every shipped fixed-size display). Full suite **7211 passed / 0 failed / 5 skipped**, warning-clean. Pre-merge review: **no findings** (all 5 high-risk surfaces clean — closure-capture, the no-op `SetActive` guard, the single-source byte-for-byte invariant, the buffer-size consistency window, gate un-fakeability). UAT (live web stack, single-source path): 12 consecutive `FB` frames stable at 256×192 / 196616 bytes — the re-size never spuriously fired; the multi-source switch is covered by the deterministic `MachineHostResizeTests` gate. **STOP per protocol:** the next eligible rows — **N** (Videx, deps A,M ✅), **O** (deps K,N), **P** (ST status), **Q** (disk affordance, deps F/G) — are all `JIT`-unplanned. Planner plans N+O (the Videx/CP/M-display path) next, grounded against shipped M. Planner — **rows K + M PLANNED** (next batch). **K** ([plan](superpowers/plans/2026-06-20-apple2-pr-k-cpm-boot.md), the dual-CPU **capstone**, deps E/F/H/J ✅): composes the shipped seams into the SoftCard board — `Apple2Board.SpecWithSystem` + `WithCoprocessor(Z80)` via `CoprocessorSpec` + the shipped `SoftCardTranslation` + `SoftCardControlPort` → `SoftCardBoard.Spec`; adds the CP/M data-track skew (`SectorOrderKind.Cpm`, research §5) to the shipped `.dsk` adapter, the `SoftCardCpm` 143,360-byte `.dsk` loader, the `SoftCardSurface` (CP/M in drive 1), and `get-softcard-cpm.{sh,ps1}` (Asimov mirror, never vendored, sign-off GIVEN). Gate: the real 6502 `$C600`→tracks→`$CnXX` boot hands off to the Z80, CP/M runs **translated** to `A>` on the Apple 40-col render (interpreter tier; the Videx 80-col is PR-N/O) — **asset-gated, skip-with-note** when the `.dsk` (or system ROM) is absent (a skipped gate is GREEN). **M** ([plan](superpowers/plans/2026-06-20-apple2-pr-m-display-multiplexer.md), no deps): the ADR 0016 Decision 1 active-display seam — `DisplayMultiplexer : IDisplayDevice` (Core, delegates `Width`/`Height`/`RenderInto`/`FrameReady` to the active source, `SetActive` fires `FrameReady`) + the one `MachineHost` change (drop `readonly` on `_rgba`, re-size to the active source's geometry per frame). Gate: switching the active source makes the host re-pull at the new size; the single-source path is byte-for-byte unchanged. Plans grounded against `main` @ `10f5737` (PRs #99–#111). **Both are immediately Builder-eligible** — K (deps E/F/H/J ✅), M (no deps); take the lower id (K) first per the queue rule. **N (Videx) + O (CP/M-on-Videx) stay `JIT`** — planned next against shipped M (N needs M) / K + N (O). **Next: Builder picks up K (or M).**). (Builder — **dual-CPU arc batch 1 SHIPPED (rows I + J)**. **I** (dual-CPU `Machine`/`MachineBuilder` scaffolding, PR #110): the single-CPU path is **provably byte-for-byte unchanged** (every pre-existing test still passes). **J** (`SoftCardTranslation` 6-branch table + `SoftCardControlPort`, PR #111): a real Z80 runs translated against shared 6502 RAM, the 6-branch boundary regression kills the refuted `+$1000` shortcut at branches 2–5, full suite **7196 passed / 0 failed / 4 skipped** (+25 new PR-J tests, purely additive — no shipped source touched). **K** (CP/M boot, deps E/F/H/J — now all ✅) is the topmost 📋 but its Plan is `JIT`-unplanned → **Builder STOPS per protocol; Planner plans K next** (grounded against shipped I/J). Planner — **dual-CPU arc batch 1 (rows I + J) PLANNED**: ADR 0015's biggest abstraction is now bite-sized. **I** ([plan](superpowers/plans/2026-06-20-apple2-pr-i-dual-cpu-scaffolding.md)) extends the shipped single-CPU machine model to two CPUs over one shared program space — `CoprocessorSpec?` on `BoardSpec`, `WithCoprocessor`, `IAddressTranslation`/`TranslatingAddressSpace`/`ICoprocessorControl`, the run-one-then-the-other dual-CPU `Run` (6502-domain virtual clock, all-IRQ-to-primary, dormant core never scheduled), with the **single-CPU path byte-for-byte unchanged** as the load-bearing regression gate. **J** ([plan](superpowers/plans/2026-06-20-apple2-pr-j-softcard-translation.md)) adds the concrete `SoftCardTranslation` (the 6-branch MAME-verified table — the refuted `+$1000` shortcut fails branches 2–5; 1 & 6 coincide), `SoftCardControlPort` ($CnXX-write active-CPU toggle, peek-free), with a real Z80 routine running translated against shared 6502 RAM as the end-to-end gate. Plans grounded against `main` @ `d685b0c`. **I is immediately Builder-eligible** (dep A ✅); **J follows I**. K (CP/M boot) stays `JIT` — planned against shipped I/J next. **Next: Builder picks up I.**). **Owner:** Mark.
> **Producer:** Claude Planner (writes specs + plans, appends rows). **Consumer:** Claude Builder
> (claims a 📋 row whose dependencies are all ✅, ships one PR per cycle, marks it ✅, loops).
>
> This is the single dispatch list for the **Apple ][+ emulation arc** (ADRs 0014/0015/0016 +
> `docs/superpowers/specs/2026-06-20-apple-2-plus-design.md`). The design space is **settled** — these
> rows are a decomposition into shippable, gated PRs, not an invitation to re-litigate decisions. Owner
> decisions are baked in (see **Locked decisions** below); do not reopen them.

---

## How to use this queue (Builder)

1. Pick the **topmost 📋 queued** row whose **every** dependency (`Deps`) is ✅ done. Do not reorder; the
   sequence is owner-set. If two rows are both eligible, take the lower id.
2. If the row's **Plan** column says `JIT` (just-in-time), there is **no detailed plan yet** — the row
   is queued but not planned. **Stop and tell the owner** the item is at the front and needs a Planner
   pass before you implement. Builder does not author the bite-sized plan; Planner does. Rows with a
   plan link (`plans/2026-06-20-apple2-*.md`) are ready to implement now.
3. Branch (`feat/apple2-<topic>`), implement the plan task-by-task, run the row's **un-fakeable gate**,
   open the PR, merge on green gates (per the auto-merge policy in `CLAUDE.md`), set the row to ✅, loop.
4. Update the **Last updated** banner when you change a status.

**Status legend:** 📋 queued · 🔨 in-flight (Builder claimed) · ⛔ blocked (a dep is not done / owner
input needed) · ✅ done (PR merged) · ⏸️ deferred (intentionally not now).

**Interpreter-first invariant.** Every row ships + gates on the **interpreter tier** (the oracle). JIT
emit under any new seam (the `Remap` listener, the Z80-under-translation fastmem) is a *separate*,
*separately-gated* follow-on row — never a blocker for the interpreter-correct deliverable.

---

## Locked decisions (do NOT reopen — owner-accepted, Coordinator session 2026-06-20)

- **`Remap` lives on `IAddressSpace`** (settles ADR 0009 OQ4; PR-A builds it there).
- **Disk II: full `.woz`/LSS fidelity UPFRONT** — woz/nibble track-bitstream controller is the *primary*
  path; the `.dsk`/`.po` re-nibblizing adapter folds into the same track-bitstream seam. **No
  sector-first staging.** The `IFluxImage`-style seam sits beside `IBlockDevice` from the start.
- **Assets fetch-on-demand, never vendored** (`get-apple2-roms` / `get-videx-roms` / `get-softcard-cpm`,
  cache outside source control, skip-with-note when absent). SoftCard CP/M sign-off is **GIVEN** (fetch
  from the Asimov mirror on demand).
- **Disk loading UX = BOTH** a cached-library dropdown **and** a per-drive upload picker.
- **Design defaults accepted:** upload transport = WS binary frame; uploaded disks session-scoped (no
  persistence); no per-drive Boot button (RESET-with-disk); name the `.sh` in fetch copy; skip the
  control-strip pixel-polish pass.

---

## Queue

| id | Title | Status | Deps | Plan | Un-fakeable gate (interpreter, no asset needed unless noted) |
|---|---|---|---|---|---|
| **A** | `AddressSpace.Remap` seam + JIT invalidation listener | ✅ | — | [plan](superpowers/plans/2026-06-20-apple2-pr-a-remap-seam.md) | A mapped range re-pointed by `Remap` reads the new backing; `RemapPeripheral` re-points to MMIO; `OnRemap` fires with the right page span; `BlockCache.InvalidatePages` evicts only those pages; no current device's behavior changes (regression). |
| **B** | `Apple2Board` BoardSpec skeleton + `Apple2Iou` soft-switch decoder | ✅ | A | [plan](superpowers/plans/2026-06-20-apple2-pr-b-board-and-iou.md) | The board validates + builds; the IOU owns the `$C000` page; `$C050–$C057`/`$C030` toggle on **any access** (read OR write) identically; `TryPeek` has **no** side effect (peek-free); the speaker double-toggles on a write opcode. |
| **C** | `Apple2Video` (`IDisplayDevice`): text / lo-res / hi-res render | ✅ | B | [plan](superpowers/plans/2026-06-20-apple2-pr-c-video.md) | `RenderInto` reproduces the verified hi-res `addr(y)` landmarks (y=0→`$2000`, y=1→`$2400`, y=8→`$2080`, y=64→`$2028`, y=191→`$3FD0`) + the GBASCALC text row bases, reading live main RAM into RGBA. Synthetic RAM, no ROM. |
| **D** | `Apple2Keyboard` (`IKeyboardSink`) + `Apple2Speaker` (`IAudioSink`) | ✅ | B | [plan](superpowers/plans/2026-06-20-apple2-pr-d-keyboard-speaker.md) | `$C000` returns the latch (bit7 strobe + ][+ code), `$C010` clears strobe; `PostKey` folds to the uppercase-only ][+ set; `$C030` toggle log → S16 PCM both polarities + level-carry (the Spectrum beeper gate shape). |
| **E** | Language Card mapper (`$C080–$C08F`) — first `Remap` consumer | ✅ | A, B | [plan](superpowers/plans/2026-06-20-apple2-pr-e-language-card.md) | Two consecutive odd-`$C08x` reads write-enable `$D000–$FFFF` RAM (one read does not); bank-1/bank-2 + read-ROM/read-RAM select correctly; each switch calls `Remap` and (JIT) evicts the banked pages; runs code out of LC RAM. |
| **F** | Disk II controller — `.woz`/LSS nibble path + `IFluxImage` seam | ✅ | B | [plan](superpowers/plans/2026-06-20-apple2-pr-f-disk-ii-woz.md) | The LSS sequencer produces the 6-and-2 GCR nibble stream a guest poll reads at `$C0EC`; stepper/motor soft switches drive head + the ~1 s 556 motor-off delay; `Fine` timing. The `IFluxImage` track-bitstream seam sits beside `IBlockDevice`. Synthetic `.woz` track, no ROM. |
| **G** | Disk II — `.dsk`/`.po` re-nibblizing adapter | ✅ | F | [plan](superpowers/plans/2026-06-20-apple2-pr-g-disk-dsk-adapter.md) | A `.dsk`/`.po` logical-sector image re-nibblizes into a synthetic track on the **same** `IFluxImage` path PR-F reads — the controller is format-agnostic above the seam. Synthetic `.dsk`, no ROM. |
| **H** | `Apple2Surface` + `get-apple2-roms.{sh,ps1}` + ROM-boot gate | ✅ | C, D, E, F, G | [plan](superpowers/plans/2026-06-20-apple2-pr-h-surface-and-rom-boot.md) | With the system + char-gen ROMs fetched, the ][+ boots to the Applesoft `]` prompt (text-screen RGBA assertion) on **both** tiers; DOS 3.3 boots from a `.dsk` in drive 1. **Asset-gated** (skip-with-note absent). |
| **I** | Dual-CPU `Machine` / `MachineBuilder` scaffolding (`CoprocessorSpec`) | ✅ | A | [plan](superpowers/plans/2026-06-20-apple2-pr-i-dual-cpu-scaffolding.md) | `CoprocessorSpec` + `WithCoprocessor` + the dual-CPU `Run` build a 2-CPU machine; the **single-CPU path is byte-for-byte unchanged** (every existing board regression-identical); all interrupts route to the primary 6502; the dormant core is never scheduled. |
| **J** | `SoftCardTranslation` (6-branch table) + `TranslatingAddressSpace` + `SoftCardControlPort` | ✅ | I | [plan](superpowers/plans/2026-06-20-apple2-pr-j-softcard-translation.md) | All **6** translation branches assert at their boundaries (`$AFFF→$BFFF`, `$B000→$D000`, `$EFFF→$CFFF`, `$F000→$0000`, …) — the refuted `+$1000 mod 64K` shortcut fails branches **2–5** (branches 1 & 6 coincide); the control-port write flips `_z80Active` and ends the slice. |
| **K** | Interpreter-tier CP/M boot wiring (`$C600`→tracks→`$CnXX`-start) | ✅ | E, F, H, J | [plan](superpowers/plans/2026-06-20-apple2-pr-k-cpm-boot.md) | The real SoftCard boot sequence (6502 `$C600` reads tracks `$00–$02`, sets LC banking, writes `$CN00`) hands off to the Z80; CP/M reaches its load state on the **interpreter** tier. **Asset-gated** on the SoftCard CP/M `.dsk` (skip-with-note absent). |
| **L** | JIT-under-translation (pre-translated physical fastmem) | ⏸️ | K | JIT | *(deferred/optional, ADR 0015 Decision 4 — measure interpreter CP/M throughput first.)* The Z80-under-translation gets fastmem over the physical backing arrays; parity-gated against the running interpreter SoftCard (the oracle). |
| **M** | `DisplayMultiplexer` + `MachineHost` per-frame re-size | ✅ | — | [plan](superpowers/plans/2026-06-20-apple2-pr-m-display-multiplexer.md) | The multiplexer delegates `Width`/`Height`/`RenderInto`/`FrameReady` to the active source; `SetActive` fires `FrameReady`; `MachineHost` re-sizes its `_rgba` buffer when dimensions change; a single-display board is transparent (no behavior change). |
| **N** | `VidexVideoterm` (`IPeripheral`+`IDisplayDevice`) + `$C800` expansion-bank mapper | 📋 | A, M | [plan](superpowers/plans/2026-06-20-apple2-pr-n-videx-videoterm.md) | The 6845 CRTC programmed via `$C0B0`/`$C0B1` + the init table (R1=`$50`/R6=`$18`) yields 80×24; VRAM+synthetic-charROM → RGBA; the `$C800` mapper (2nd `Remap` consumer) banks the firmware window + the `$CC00–$CDFF` VRAM bank; the Videx's `ActiveChanged` makes a `DisplayMultiplexer` `SetActive`. Synthetic char ROM, no asset. *(`IFastMemoryProvider` dropped — ADR-0009-designed, never shipped; the fast-RAM intent via `Remap`-to-RAM. See plan drift note.)* |
| **O** | Videx + CP/M asset scripts (`get-videx-roms`, `get-softcard-cpm`) + CP/M-on-Videx end-to-end gate | 📋 | K, N | [plan](superpowers/plans/2026-06-20-apple2-pr-o-cpm-on-videx.md) | With all assets fetched, booting the CP/M disk widens the display to the **80-col Videx terminal** (the `DisplayMultiplexer` auto-switches Apple-40 → Videx-80, guest-driven) and reaches the `A>` prompt — **interpreter-tier** (the row's "both tiers" is imprecise; the CP/M/Z80 side is interpreter-only per PR-K/ADR 0015 D4). **Asset-gated + owner-sign-off-given** (skip-with-note absent). |
| **P** | The `ST` status-frame seam (host→client read-only indicators) | 📋 | — | JIT | A new lightweight `ST` wire frame carries board name, asset state, per-drive motor + image label, video-mode label; the client renders them read-only; the host pushes real machine state (not faked). *(Designer T-A — suggested early; most surface indicators consume it.)* |
| **Q** | In-session disk insert / eject mechanism (Disk II runtime image swap) | 📋 | F, G | JIT | The Disk II controller accepts "load these bytes as drive N's image" + "eject drive N" at runtime, for both `.woz` and `.dsk`/`.po`, via the `IFluxImage` seam; a running machine swaps images without rebuild. *(Designer T-D — shared dep of the two disk-UX paths.)* |
| **R** | `GET /disks` catalog endpoint + per-drive library dropdown | 📋 | Q | JIT | The server lists the cached `disks/` images (name, format, drive-compat, CP/M grouping); both per-drive `[ Library ▾]` selects populate from it; an empty catalog disables the select with the named-script hint. *(Designer T-C.)* |
| **S** | Disk-upload inbound-binary path (the NEW binary WS frame + validation + UPLOADING state) | 📋 | Q | JIT | Client `<input type=file>` → client validation (ext / 2 MB cap / non-empty) → binary WS `DK` frame → **server** re-validation (`.woz` magic / `.dsk`/`.po` exact length) → load into drive N; the UPLOADING → INSERTED / error states drive the panel. *(Designer T-B — the surface's first inbound binary path; explicitly its own task.)* |
| **T** | Control-strip UI (drive panels, lights, mode label, asset banner) | 📋 | P, R, S | JIT | Two bordered drive panels (library select + upload + eject + a real-motor amber light driven by `$C0E8/$C0E9` + the 1 s off-delay, **not** faked on insert); the calm named-script asset banner replaces the silent fallback; one new `--drive-active` token. *(Designer T-E/T-G/T-H + keyboard extensions T-F.)* |

---

## Per-row notes, dependencies, and just-in-time planning

**Planned now (ready for Builder):** **A, B, C, D, E, F** (shipped) plus **G + H** now have detailed
bite-sized plans (`docs/superpowers/plans/2026-06-20-apple2-pr-{a..h}-*.md`). **G is immediately
Builder-eligible** (dep F ✅); **H follows G** (deps C, D, E, F ✅ + G). The G + H plans are grounded
against the actually-shipped PR-A..F source at `main` @ `c2ae005`: G's `DskFluxImage` re-nibblizes onto
the shipped `IFluxImage` seam + composes the shipped `Apple2Gcr` table with **no controller/IOU/board
change** (the OQ1-✅ format-agnostic invariant); H mirrors the shipped `SpectrumSurface`/`SpectrumRom`/
`get-spectrum-rom`/`SpectrumBootTests` set verbatim, wiring the `Apple2Video`/`Apple2Keyboard`/
`Apple2Speaker` triad through `MachineHost` and gating the Applesoft `]` boot on both tiers
(skip-with-note absent). **Together G + H complete the base-machine boot milestone** (a ][+ that reaches
the `]` prompt + runs DOS 3.3). The earlier `pr-{a..f}` plans were grounded against `97a44d5`.

**Dual-CPU arc batch 1 — now planned:** **I + J** (the ADR 0015 dual-CPU scaffolding + the SoftCard
translation) now have detailed bite-sized plans (`docs/superpowers/plans/2026-06-20-apple2-pr-{i,j}-*.md`),
grounded against `main` @ `d685b0c` (PRs #99–#108). **I is immediately Builder-eligible** (dep A ✅);
**J follows I** (dep I). I extends the shipped `Machine`/`MachineBuilder`/`BoardSpec`/`BoardMachineFactory`/
`BoardSpecValidator` with the optional `CoprocessorSpec` path (additive — the single-CPU path is
byte-for-byte unchanged, the load-bearing regression gate the full suite enforces) plus the new
`IAddressTranslation`/`TranslatingAddressSpace`/`ICoprocessorControl` Core seams + the run-one-then-the-
other dual-CPU `Run`; J adds the concrete 6-branch `SoftCardTranslation` + the `$CnXX` `SoftCardControlPort`
as pure `CpuEmulator.Peripherals` additions riding I's seams. **K (CP/M boot) stays `JIT`** — it is planned
against the *shipped* I/J next, per the cadence below.

**Planned just-in-time (`Plan: JIT` above):** K–T are queued with their dependencies + un-fakeable gate
fixed, but their bite-sized plans are written **as each approaches the front of the queue** (the
established cadence — the Spectrum/M6 arcs planned in waves, not all at once). When a `JIT` row becomes
the topmost eligible item, Builder stops and asks Planner for the detailed plan. This keeps each plan
grounded against the *then-current* `main` (e.g. PR-E's plan is written after PR-A has actually landed
the `Remap` API, so its literal code calls the real shipped signature; the I/J plans were written after
PR-H landed, so they call the real shipped machine-model signatures).

### Dependency rationale (the valid build order)

- **A first, always.** The `Remap` seam (ADR 0014 Decision 4 / ADR 0009 OQ4) is the one framework
  primitive the arc adds; the Language Card (E), the Videx `$C800` mapper (N), and the dual-CPU
  scaffolding (I) all consume or sit beside it. It touches no Apple code — pure `Core`/`Jit`.
- **B gates C/D/E/F.** The board skeleton + the IOU decode seam is what every Apple peripheral plugs
  into. C (video), D (keyboard/speaker), E (LC ports), F (disk ports) all delegate through the IOU.
- **The base-board ROM-boot gate (H) needs C+D+E+F+G** — a real Applesoft `]` + DOS boot exercises
  video, keyboard, speaker, the Language Card (DOS lives in LC RAM), and Disk II together.
- **The dual-CPU arc (I→J→K)** sits on A (it reuses the LC `Remap` for the Z80's `$B000`/`$D000` view)
  and, for the CP/M boot (K), on the base board's disk + LC + ROM boot (E, F, H) plus the translation
  (J). **L (JIT-under-translation) is deferred** — ship interpreter CP/M first, measure, then decide.
- **The CP/M-display arc (M→N→O)**: the multiplexer (M) is independent framework; the Videx (N) needs A
  (the `$C800` mapper is the 2nd `Remap` consumer) + M (it is one multiplexer source); the end-to-end
  gate (O) needs the CP/M boot (K) + the Videx (N).
- **The surface arc (P, Q, R, S, T)**: the `ST` frame (P) and the runtime disk-swap mechanism (Q) are
  the shared seams; the library dropdown (R) and the upload path (S) both depend on Q; the control-strip
  UI (T) composes P + R + S + the keyboard extensions. P and Q can start early (P depends on nothing
  hard; Q depends on the disk controller F/G). These are client + thin-server tasks; they do **not** gate
  the emulation-core arc and can interleave once their deps land.

### Owner-input items before Builder clears past the foundation

- **None block PR-A/B/C.** The owner decisions are all baked in above; the foundation is fully specified.
- **Char-gen ROM inventory (ADR 0014 Decision 7 / research §-residual 2):** the exact char-gen ROM size
  + source is a build-time follow-up. PR-C ships a **built-in fallback glyph set** so the text-render
  gate runs without the ROM; PR-H's `get-apple2-roms` script adds the char-gen fetch with a
  length-sanity-check when the canonical source is confirmed. Flag to owner at PR-H, not before.
- **CP/M licensing (ADR 0016 Decision 5):** sign-off is **GIVEN** (fetch-on-demand from the Asimov
  mirror). No further owner gate — but PR-O's gate stays skip-with-note when the asset is absent.

---

## Recently shipped (Apple ][+ arc)

- **PR-M — `DisplayMultiplexer` + `MachineHost` per-frame re-size (the active-display seam)**
  (2026-06-21, PR #114). The ADR 0016 Decision 1 active/overriding-display-source seam — the mechanism the
  CP/M-display arc (the Videx 80-col, PR-N/O) plugs into. **Two additive changes, no Videx.**
  **`DisplayMultiplexer : IDisplayDevice`** (new, `CpuEmulator.Core`, alongside `IDisplayDevice`): wraps an
  ordered list of N source `IDisplayDevice`s + an active index; `Width`/`Height`/`RenderInto` delegate to the
  active source; **`FrameReady`** forwards the active source's event AND fires on a `SetActive` switch (so the
  surface re-pulls — and re-sizes — at the new geometry). The ctor subscribes **every** source's `FrameReady`
  with a **per-iteration captured index** (no closure-over-loop-variable trap), re-raising only when the
  firing source is the **active** one — a dormant source's frames are dropped (the host only ever pulls the
  active source; rendering a dormant frame would write the wrong geometry). **`SetActive(int)`** validates the
  index, swaps the active source, and raises `FrameReady` **only on an actual change** (a no-op re-select is
  silent). With one source the multiplexer is **transparent** (every frame forwards, `SetActive(0)` is inert).
  The guest-driven caller is PR-N's Videx (its `$C800`-enable state) — M ships only the mechanism + gates with
  test-double sources. **`MachineHost` re-size** (the one required change): drop `readonly` on `_rgba`, add
  `EnsureFrameBuffer()` before `RenderInto` in `Step` — re-allocate `_rgba` to `_display.Width*_display.Height`
  **only when that product changed**. A **strict no-op for every shipped fixed-size display** (Apple2Video
  280×192, SpectrumUla 256×192, demo 256×192 — the single-source path is byte-for-byte unchanged, the
  load-bearing regression), a one-time realloc on the rare active-source switch; the buffer re-size + the
  `FrameCodec.EncodeFrame(_display.Width, _display.Height, _rgba)` read the **same** `_display` within one
  `Step` (no consistency window), and the wire frame already carries per-frame width/height so the client
  re-sizes its canvas automatically (no client change). Dropping `readonly` is thread-safe-neutral (`_rgba` is
  written only in the ctor + `EnsureFrameBuffer`, both single-threaded inside `Step`; `_frameDirty` is the
  `volatile` cross-thread flag). Pre-merge review (focused on the single-source invariant, the FrameReady
  forwarding + closure capture, the `SetActive` no-op guard, the buffer-size consistency window, and the
  gate's un-fakeability) found **no issues at any severity** — confirmed the re-size is a true no-op for all
  fixed-size displays, the index capture is per-iteration, the no-op guard is tested, and exception types
  match. **No fixer needed.** **The un-fakeable gates:** (1) `DisplayMultiplexerTests` (6) — delegation,
  switch-fires-FrameReady, active-only forwarding, single-source transparency, bounds; (2)
  `MachineHostResizeTests` (2) — the host re-pulls at the new size on a source switch (280×192 → 720×216:
  without the re-size the larger `RenderInto` throws on the undersized span — unfakeable) + the single-source
  buffer never re-sizes (5 frames, all 256×192, no realloc — the byte-for-byte regression). **UAT** (live web
  stack, single-source path): 12 consecutive `FB` frames stable at 256×192 / 196616 bytes — the re-size never
  spuriously fired on a fixed-size source; zero server errors; `WebServerSmokeTests` green. Gate: full suite
  **7211 passed / 0 failed / 5 skipped** (the 7203 post-PR-K baseline + 8 new PR-M tests, purely additive — no
  shipped surface touched), warning-clean. **The active-display seam is ready for PR-N's Videx.** Unblocks
  **N** (the Videx Videoterm is one multiplexer source; deps A ✅ + M) → **O** (CP/M-on-Videx end-to-end).
- **PR-K — Interpreter-tier CP/M boot wiring (`$C600`→tracks→`$CnXX`-start) — the dual-CPU capstone**
  (2026-06-21, PR #113). Composes the shipped dual-CPU seams (PRs A–J) into the **Microsoft Z-80 SoftCard
  board** + wires the real CP/M boot path. **Pure composition + three new data points** — no shipped
  dual-CPU machinery re-implemented. **`SectorOrderKind.Cpm`** (the new datum in `Apple2SectorOrder`): the
  canonical CP/M data-track skew `[0, 6, 12, 3, 9, 15, 14, 5, 11, 2, 8, 7, 13, 4, 10, 1]` (research §5) —
  a valid permutation distinct from the shipped DOS 3.3 / ProDOS tables; the shipped `DskFluxImage`
  re-nibblizes the CP/M `.dsk` onto the **unchanged** `Apple2DiskII` head with it. **`SoftCardBoard.Spec`**
  (`CpuEmulator.Machines`): takes `Apple2Board.SpecWithSystem` and `with { Peripherals = [..base, controlSlot],
  Coprocessor = coproSpec }` — `CoprocessorSpec(Z80, new SoftCardTranslation(), "softcard", 2.0)` + a
  `SoftCardControlPort` at **`$C500`** (slot 5, flush inside the `$C000-$C5FF` Mmio region the carve leaves —
  validator-clean; the control-port slot `Name` `"softcard"` equals `CoprocessorSpec.ControlPortPeripheral`,
  satisfying the `copro-control-port-unwired` check). `BoardMachineFactory` builds the Z80 on the
  **interpreter tier** (PR-I; ADR 0015 Decision 4 — JIT-under-translation is the deferred PR-L). The 6502 is
  bus master at reset; the Z80 is dormant until the boot loader's `$CnXX` write. **`SoftCardCpm`** (the
  `Apple2Rom` twin): loads + length-validates the **143,360-byte** (35×16×256) CP/M `.dsk` from
  `<cache>/cpm/softcard-cpm.dsk` as a read-only 256-byte-sector `IBlockDevice` (rejects wrong length, throws
  loud when absent). **`SoftCardSurface`** (the `Apple2Surface` twin): inserts the CP/M `.dsk` into drive 1
  (`DskFluxImage(cpm, SectorOrderKind.Cpm)`), builds the SoftCard board, Realizes the video/speaker over the
  built machine, Resets, wires `MachineHost` — on the bare board the display is the Apple 40-col video (the
  80-col Videx is PR-N/O). **`Program.cs`** boots the SoftCard when **both** the Apple system ROM and the
  CP/M `.dsk` are cached (the `.dsk` probe is lazy — only stat'd when the Apple ROM is present), else the
  existing Apple/Spectrum/demo chain unchanged; pushes the one-shot `ST softcard-cpm` text frame
  (`app.js` maps it to `"connected · Apple ][+ SoftCard · CP/M"`). **`get-softcard-cpm.{sh,ps1}`** fetch the
  `.dsk` on demand from the Asimov mirror (ADR 0016 Decision 4/5; sign-off GIVEN; **never vendored**), with a
  143360-byte length sanity-check + a placeholder-URL guard (so an unconfigured run says so plainly instead
  of an opaque DNS error). Pre-merge review (focused on the skew-table correctness, the board-composition
  self-consistency, the loader bounds, the surface lifecycle, the boot-branch ordering, and the gate's
  un-fakeability) found **no correctness bugs** — 1 MEDIUM (the placeholder fetch URL, by-design per the plan
  — mitigated with the guard) + 1 LOW (the lazy `.dsk` probe) both fixed. **The un-fakeable gate** (interpreter
  tier only — the coprocessor has no JIT path): with the assets present, the real 6502 `$C600` Autostart
  boot reads the CP/M boot tracks (the `DskFluxImage` synthesizes the GCR with the CP/M skew), the on-disk
  cold-boot loader issues the `$CnXX` write that flips `_z80Active`, and the **real Z80** runs CP/M
  **translated** against shared RAM (PR-J's end-to-end proof) to the `A>` prompt — asserted as structural ink
  on a mostly-blank Apple 40-col text render + `machine.CoprocessorActive` (the load-bearing dual-CPU-handoff
  proof — a 40-col-only Applesoft boot leaves it false) + a committed-hash placeholder (inert until captured).
  **Asset-gated, skip-with-note** when the `.dsk` (or system ROM) is absent (the PR-H discipline — a skipped
  gate is GREEN); in this environment the gate **SKIPPED**, so the **live CP/M-to-`A>` is pending the fetched
  asset** (the owner runs `get-softcard-cpm` + `get-apple2-roms`). **Web-surface UAT** (no browser MCP;
  HTTP+WS frame level, the PR-H depth): the server boots, `GET /`+`/app.js` 200, the WS upgrades (101), the
  `ST` text frame leads, `FB` binary frames stream at 256×192, and **zero server errors** — the modified
  `Program.cs` did not regress the Apple/Spectrum/demo fall-through (assets absent → SoftCard branch inert,
  demo board paints, exactly as designed). Gate: full suite **7203 passed / 0 failed / 5 skipped** (the new
  CP/M skew + board + loader + surface tests green; the CP/M boot gate + 4 pre-existing asset-gated skips),
  warning-clean. **The dual-CPU arc's interpreter-tier CP/M boot is complete.** The next CP/M milestone is the
  80-col display: **M** (the display multiplexer) → **N** (the Videx Videoterm) → **O** (CP/M-on-Videx
  end-to-end). **L** (JIT-under-translation) stays deferred (measure interpreter CP/M throughput first).
- **PR-J — `SoftCardTranslation` (the 6-branch table) + `SoftCardControlPort` (the `$CnXX` active-CPU
  toggle)** (2026-06-21). The concrete Z80→Apple address translation + the control port that drives the
  active-CPU handoff — **pure `CpuEmulator.Peripherals` additions riding PR-I's seams** (zero shipped-source
  change; 4 new files only). **`SoftCardTranslation : IAddressTranslation`** (ADR 0015 Decision 3 / research
  §2, the MAME-verified `a2softcard.cpp` table) is a 6-way branch on the top nibble of the 16-bit logical
  address: **branch 1** (`$0000–$AFFF` → `+$1000`, the only true additive arm); **branches 2–6** mask the
  low 12 bits and add a 4 KiB-window base (`$B000`→`$D000`, `$C000`→`$E000`, `$D000`→`$F000`, `$E000`→`$C000`,
  `$F000`→`$0000`) so CP/M's zero page/TPA land on usable RAM while the Apple's immovable regions shuffle to
  the top of the Z80 map. The DIP-switch S1-1 disable makes it the identity (construction-time, defaulted on).
  **The refuted `+$1000 mod 64K` shortcut is structurally killed**: it coincides with the real table on
  branches 1 **and** 6 (`($F000+$1000) mod 64K = $0000`) — expected, not a bug — so the boundary regression
  asserts the exact physical address at **all six** branches AND adds explicit `NotEqual(shortcut, real)` at
  **branches 2–5** (the four shortcut-killers). **`SoftCardControlPort : IPeripheral`** is the slot's `$CN00`
  control register: a write (or any access — research §1 "the decoder fires on any access", so `Read` mirrors)
  **flips** which CPU is bus master via `ICoprocessorControl` (captured from the `Realize` context, since the
  dual-CPU `Machine : IMachineContext` implements `ICoprocessorControl`); from 6502 mode a `$CN00` write hands
  off to the Z80, the Z80's matching write (which it sees as `$EN00`, translated back by branch 5) hands back.
  **Peek-free** (the ][+ invariant, ADR 0014 Decision 2): `TryPeek` returns honest open-bus `(true, 0)` with
  no toggle — and returning `true` is the deliberate signal that stops the debugger from falling through to
  the side-effecting `Read`. On a single-CPU board the `ICoprocessorControl` cast fails and the port is inert
  (never an exception). Pre-merge review (focused on the 6-branch boundaries, the peek-free invariant, and
  whether the end-to-end gate genuinely proves translated execution) found **no HIGH/MEDIUM/LOW issues** and
  confirmed the table correct at every boundary, the peek-free invariant satisfied, and the gate unfakeable —
  **no fixer needed.** **The un-fakeable gate** (interpreter tier): (1) the 6-branch boundary regression (12
  boundary cases + 4 shortcut-killers + 4 DIP-identity); (2) a translated-view composition over shared RAM
  (branches 1/2/6 end-to-end through `TranslatingAddressSpace`); (3) the **real end-to-end** — a real dual-CPU
  SoftCard-shaped board built through `BoardMachineFactory`: a real 6502 `STA $C200` hands off, then a **real
  Z80** (reset PC=0 → physical `$1000` via branch 1) runs `LD A,$42 / LD ($F000),A / JR -2` and writes `$42`
  to Z80 `$F000` → physical `$0000` (branch 6); the 6502 reads physical `$0000` and sees `$42` (the Z80 ran
  **through the translation against the shared RAM**), and the suspended 6502 does **not** advance. **No
  browser UAT** — backend/library change, no UI surface; the un-fakeable gates + full-suite green are the
  substitute per the auto-merge policy. Gate: full suite **7196 passed / 0 failed / 4 skipped** (the 7171
  post-PR-I baseline + 25 new PR-J tests, purely additive), warning-clean. **The dual-CPU arc's translation
  layer is complete.** Unblocks **PR-K** (the interpreter-tier CP/M boot — now `JIT`-unplanned and at the
  front of the queue; the Planner plans it next against shipped I/J).
- **PR-I — dual-CPU `Machine` / `MachineBuilder` scaffolding (`CoprocessorSpec`) — the dual-CPU arc's
  load-bearing abstraction** (2026-06-21). Extends the shipped single-CPU machine model to express **two
  CPUs sharing one program space** (ADR 0015 Decisions 1, 2, 5, 6, 7) — **additively**, with the
  **single-CPU path provably byte-for-byte unchanged** (the load-bearing regression gate). Three new Core
  seams: **`IAddressTranslation`** (`uint ToPhysical(uint logical)` — the coprocessor's logical→primary
  physical map), **`TranslatingAddressSpace`** (an `IAddressSpace` wrapper the coprocessor core is
  constructed over — Read8/Write8/TryPeek8 route through `ToPhysical`, the default-interface wide accessors
  compose over them for correct page-wrap, and `MapMemory`/`MapPeripheral`/`Remap`/`RemapPeripheral` throw
  `NotSupportedException` so a mis-wire is loud), and **`ICoprocessorControl`** (`SetCoprocessorActive(bool)`
  — the active-CPU toggle seam the `Machine` implements and a control port consumes via its `Realize`
  context, since `Machine : IMachineContext`; a clean seam, **not** a cast). **`Machine`** gains the
  dual-CPU construction path (a second `ICpuCore` built over the `TranslatingAddressSpace` via a private
  `CoprocessorContext` adapter that returns the wrapper for `Space(Program)` and forwards everything else;
  interrupts bind to the **primary only** — Decision 5) and the dual-CPU **`RunDualCpu`** (run-one-then-the-
  other: drives **only** the active core, never schedules the dormant one — Decision 1; the scheduler runs
  in the **primary 6502 cycle domain** with the coprocessor's run time converted by `ClockRatioToPrimary`
  via a virtual clock; a control-port write ends the slice; a pending interrupt forces a switch back to the
  primary). When `Coprocessor is null` the ctor + the renamed-but-identical **`RunSingleCpu`** take the
  **exact** pre-PR-I path. **`MachineBuilder.WithCoprocessor`** carries the declaration; **`CoprocessorSpec`**
  (the optional `BoardSpec.Coprocessor` field, default `null`) declares it; **`BoardMachineFactory`** wires
  it, building the coprocessor on the **interpreter tier regardless of board tier** (ADR 0015 Decision 4 —
  the JIT's `(AddressSpace)ctx.Space(...)` cast throws on the wrapper; JIT-under-translation is deferred to
  PR-L); **`BoardSpecValidator`** adds three coprocessor checks (`copro-control-port-unwired`,
  `copro-bad-clock-ratio`, `copro-no-translation`). Pre-merge review (focused on the single-CPU-unchanged
  invariant, the virtual-clock math, the run-loop termination + interrupt-forces-primary logic, and AOT
  cleanliness) found **no HIGH/blocking issues** and confirmed by code-reading that the single-CPU branch is
  genuinely the old code (not just trusting the green suite). Three review fixes applied (one comment-clarity
  + two coverage tests — the wrapper `MapPeripheral`-throws path and the `copro-no-translation` diagnostic).
  **The un-fakeable gate** (interpreter tier): a **two-core toy board** built through `BoardMachineFactory`
  (6502 primary + Z80 coprocessor + identity translation + a control-port stub) — a real 6502 `STA $C000`
  hands off, the active CPU flips, the Z80 then runs while the suspended 6502 does **not** advance, and the
  dormant core is never scheduled — plus the **load-bearing byte-for-byte regression**: a representative
  shipped board carries `Coprocessor is null` + the single-CPU `Run` is deterministic across two identical
  builds. **The byte-for-byte single-CPU gate is GREEN: every one of the 7153 pre-existing tests still
  passes** (full suite **7171 passed / 0 failed / 4 skipped** — the 7153 prior + 18 new PR-I tests; the 4
  skips are the same pre-existing asset/JIT-gated skips), warning-clean. **No browser UAT** — this is a
  backend/library change with no UI surface; the full-suite regression gate is the un-fakeable substitute
  per the auto-merge policy. Unblocks **PR-J** (`SoftCardTranslation` + `SoftCardControlPort` ride these
  seams) → **PR-K** (the CP/M boot).
- **PR-H — `Apple2Surface` + `get-apple2-roms.{sh,ps1}` + the ROM-boot gate (the base-machine boot
  milestone)** (2026-06-21). The arc's **first UI-touching surface PR** — the base ][+ now boots to the
  Applesoft `]` prompt (ROM present) or the calm SP0-demo fallback (ROM absent). **`Apple2Rom`** (the
  `SpectrumRom` twin) loads the three cached ROMs from `<cache>/apple2/` with exact-length validation: the
  12 KiB system ROM (required — its absence is the fallback trigger), the 256 B slot-6 Disk II boot ROM,
  and the **optional** 2 KiB char-gen ROM (missing is non-fatal — `Apple2Font.Fallback` drives render).
  **`Apple2Board.SpecWithSystem`** maps the slot-6 **`$C600`** boot ROM by carving the `$C000–$CFFF` I/O
  band into three validator-clean tiles (`$C000–$C5FF` Mmio / `$C600–$C6FF` Rom / `$C700–$CFFF` Mmio) so
  the Autostart slot-scan finds a disk while the IOU still owns the `$C000` soft-switch page; the existing
  `Spec`/`SpecWithLanguageCard`/`SpecWithDiskII` overloads are untouched (additive only).
  **`Apple2Surface`** (the `SpectrumSurface` twin) constructs the shared `Apple2VideoState`, the
  `Apple2Video`/`Apple2Keyboard`/`Apple2Speaker` triad over it (three objects, one state — unlike the
  Spectrum's single ULA), the LC + Disk II + IOU, builds the board, `Realize`s the non-board video/speaker
  chips against the live `Machine` (`Machine : IMachineContext`), resets, and wires the 6-arg
  `MachineHost`. **`Program.cs`** boots the Apple when its system ROM is cached (else the existing
  Spectrum-then-SP0-demo fallback) and pushes a one-shot **`ST <assetState>`** WebSocket **text** status
  frame on connect (the minimal precursor to PR-P's richer `ST` frame; the binary FB/AU path is untouched);
  `app.js` guards the inbound text frame before the binary `DataView` decode, renders the calm
  named-script asset banner, and adds the `Ctrl+Backspace` RESET bind; `index.html` gets the Apple title +
  the 280×192 aspect-preserving canvas. **`get-apple2-roms.{sh,ps1}`** fetch all three ROMs on-demand with
  byte-length sanity checks, **never vendoring** (Apple copyright; ADR 0014 Decision 7) — the fetch URLs
  are owner-supplied placeholders, the length check is the real correctness guarantee. Pre-merge review
  (focused on the board carve, the WS text/binary coexistence, the surface lifecycle, and the loader) found
  **no HIGH/blocking issues**; the board carve passes every `BoardSpecValidator` rule, the `ST` text frame
  can never reach `DataView` (string-guarded first), and `Realize`-then-`Reset` ordering is correct. Three
  review fixes applied: dropped a process-wide `CPUEMULATOR_TESTVECTORS` env-var mutation in the char-ROM
  test (a parallel-runner flakiness risk — now an explicit-root test seam), deferred the Spectrum-ROM probe
  to the non-Apple branch, and named both fetch scripts in the fallback banner. The implementer also caught
  + fixed a `WebServerSmokeTests` regression the new `ST` frame caused (it now reads the text frame first,
  then asserts the binary FB frame still streams — a strengthened test). The **ROM-boot gate**
  (`[Apple2RomTheory]`, both tiers) asserts the `]` prompt as structural ink on a mostly-blank text screen
  + a committed-hash placeholder, and **skips-with-note when the system ROM is absent** (the
  `SpectrumBootTests` discipline) — a skipped gate is GREEN; the live "boots to `]`" confirmation is
  **pending an owner-supplied ROM**. **UAT (ROM-absent path, real frame-level WebSocket drive):** the
  server serves `index.html`/`app.js` (200), the WS connects (101), the `ST demo` text frame is the first
  inbound message, binary `FB` frames stream (256×192 SP0-demo fallback), inbound keys are accepted without
  dropping the connection, and zero server errors. Gate: the Apple2 suite green (the ROM-boot gate skips
  as expected) + the full 7153-test suite green (7153 passed, 0 failed, 4 skipped — the ROM-boot gate +
  3 pre-existing asset-gated skips), warning-clean, the web project builds. **The base-machine boot
  milestone is complete.** Unblocks the dual-CPU arc (I→J→K) + the CP/M-display arc (M→N→O) + the surface
  arc (P, Q, R, S, T) — all next-eligible rows are `JIT`-unplanned, so the Planner plans the dual-CPU arc.
- **PR-G — Disk II `.dsk`/`.po` re-nibblizing adapter (`DskFluxImage : IFluxImage`)** (2026-06-21). The
  `.dsk`/`.po` logical-sector → synthetic-GCR-track adapter that folds into the **same `IFluxImage`
  track-bitstream seam PR-F shipped** (ADR 0014 Decision 6 + OQ1-✅ — full `.woz`/LSS fidelity upfront,
  the `.dsk`/`.po` path re-nibblizes into the *same* path). **Purely additive — zero controller/IOU/board
  change** (the format-agnostic-above-the-seam invariant): the shipped `Apple2DiskII` head cannot tell a
  re-nibblized `.dsk` from a `.woz`. Three new files in `CpuEmulator.Peripherals`: **`Apple2SectorCodec`**
  ships the DOS-3.3 6-and-2 data-field nibblize (256 bytes → 342 6-and-2 bytes + 1 running-XOR checksum =
  **343** on-disk GCR bytes, the low-2-bits-bit-reversed / high-6-bits split through the **shipped**
  `Apple2Gcr.WriteTable` — no table re-derivation) + its checksum-verifying inverse + the 4-and-4
  address-field encode/decode (each MSB-set, `| 0xAA`); **`Apple2SectorOrder`** ships the DOS 3.3 (`.dsk`)
  + ProDOS (`.po`) 16-entry physical↔logical interleave tables (the CP/M skew is **deliberately deferred**
  to the CP/M arc, named in the notes); **`DskFluxImage : IFluxImage`** wraps the SP0 `IBlockDevice`/
  `DiskImage` (256-byte sectors, 16/track), exposes `TrackCount = SectorCount / 16`, and **lazily
  synthesizes** each track's nibble bitstream (16 physical sectors framed by self-sync `$FF` gaps + the
  `D5 AA 96`/`DE AA EB` address field + the `D5 AA AD`/`DE AA EB` 343-byte data field), packed MSB-first
  exactly as `SyntheticFluxImage` packs so the PR-F head reads it as-is; `IsWriteProtected` reflects the
  block device. Pre-merge review confirmed the 6-and-2 encode/decode is a **true inverse** with **no
  silent-accept path** (a corrupt field changes the XOR chain and fails the checksum), both interleave
  tables match the canonical Beneath-Apple-DOS / ProDOS sources, and the diff touches no existing source
  (a one-line thread-safety note on the pure per-track cache was the only review-driven edit). The
  un-fakeable gate runs on the **interpreter** (the oracle): a real 6502 "motor on, poll `$C0EC`, store
  every bit-7-set nibble" loop on a built `Machine`, backed by a `DskFluxImage` over the **unchanged**
  `Apple2DiskII`, captures a track's nibbles whose `D5 AA AD` data field 6-and-2-decodes to a **byte-exact**
  track-0 sector of the source `.dsk` — **synthetic `.dsk`, no ROM, no controller change.** Gate: 14 PR-G
  tests (codec round-trip + checksum-rejection + 4-and-4 + the two interleave permutations + adapter
  geometry/validity + read-back + the interpreter RWTS gate) + the full 7150-test suite green (7147
  passed, 3 pre-existing asset-gated skips), warning-clean. Unblocks PR-H (DOS-from-`.dsk` boot) + PR-Q
  (runtime disk swap, both formats).
- **PR-F — Disk II controller: the `.woz`/LSS nibble path + the `IFluxImage` track-bitstream seam** (2026-06-20).
  The project's first real disk **controller**, modeling the **LSS sequencer + the nibble bitstream as the
  primary path** (the owner decision: full `.woz`/LSS fidelity upfront — no sector-first staging). New
  **`IFluxImage`** seam in Core **beside** `IBlockDevice` (it does not modify it): a per-track bit array +
  exact bit length that loops (`TrackCount` / `TrackBits` / `TrackBitLength` / `IsWriteProtected`) — a `.woz`
  *is* this; PR-G's `.dsk`/`.po` adapter *synthesizes* one on the same path. `SyntheticFluxImage` packs nibble
  bytes MSB-first into a looping bitstream (the foundation PR-G reuses). `Apple2Gcr` ships the 6-and-2 GCR
  table (64 valid `$96–$FF` bytes, each MSB-set + ≤2 consecutive zero bits) + its round-tripping inverse.
  `Apple2DiskII : IPeripheral` is a **polled** controller (no IRQ — the byte cadence IS the polled-read model;
  **`TimingTier` is not shipped** — ADR-only — so the plan correctly avoids it): the LSS read head shifts
  track bits MSB-first until a byte with bit 7 set assembles (a `$C0EC` poll recovers nibbles); the slot-6
  soft switches drive the 4-phase stepper (head half-tracks), the motor on/off with the **~1 s 556 delay**
  (via `IScheduler.ScheduleAt` + `Cancel()`), and drive select — all **delegated by the IOU** over the
  `$C0Ex` seam (the parallel of PR-E's `$C08x`: a read's side effect rides `BusValue`, a write's rides
  `ApplyAnyAccessSideEffect`, so `Access` fires exactly once per bus access; `TryPeek` short-circuits `$C0Ex`
  so a debugger peek of `$C0EC` never advances the head — the peek-free invariant). Pre-merge review fixes:
  the stepper only re-seeks + advances the reference phase on an **actual** half-track step (an opposite-phase
  blip can't corrupt the next step's direction); the `$C0Ex` peek-free short-circuit + its gate. The
  un-fakeable gate runs on the **interpreter** (the oracle): a real 6502 "poll `$C0EC` until bit 7, store the
  nibble" loop recovers the synthetic `.woz` track's GCR bytes into RAM — no faked data, **no ROM**. The
  controller is **format-agnostic above the `IFluxImage` seam** (PR-G folds in with no controller change).
  Gate: 17 PR-F tests (GCR invariant + read head + stepper + motor delay + peek-free + the interpreter
  poll-loop) + the full 7133-test suite green. Unblocks PR-G (`.dsk`/`.po` adapter) + PR-Q (runtime disk
  swap) + PR-H (the `$C600` boot ROM slot + DOS-from-`.dsk`).
- **PR-E — Language Card mapper (`$C080–$C08F`): the first real `AddressSpace.Remap` consumer** (2026-06-20).
  `Apple2LanguageCard : IPeripheral` run-time bank-switches `$D000–$FFFF` between the system ROM and 16 KiB of
  card RAM by calling the **shipped** `IAddressSpace.Remap` (PR-A) — proving the bank-switch primitive end to
  end through a real device. The ][+ layout: `$D000–$DFFF` (4 KiB) has two RAM banks (bank 1 / bank 2,
  bit-3 / the `$C088` line); `$E000–$FFFF` (8 KiB) is one **shared** RAM region. The card holds three
  index-0-based RAM arrays + two ROM-slice arrays (the `Remap` backing is index-0-based — `BackingOffset = i<<8`
  from the passed array). The `$C08x` decode: bit 3 → bank, `(offset & 3) is 0 or 3` → read-RAM, an odd-address
  **read** arms the **two-consecutive-reads** pre-write flip-flop (one read does not write-enable; any
  non-qualifying access — a write or an even address — resets it). The IOU delegates `$C08x` (it owns the
  `$C000` page): a **write**'s side effect rides `ApplyAnyAccessSideEffect`, a **read**'s rides `BusValue`, so
  the LC's `Access` fires **exactly once** per bus access; `TryPeek` short-circuits `$C08x` so a debugger peek
  never bank-switches (the ][+ **peek-free** invariant, fixed in pre-merge review). The un-fakeable gate runs
  on **both tiers**: a real 6502 routine copied into LC RAM **executes from `$D000`** and stores `$42` — the
  interpreter is correct by re-reading the live page table; the **JIT** exercises PR-A's `OnRemap` →
  `Fastmem.Reclassify` + `BlockCache.InvalidatePages` (the LC is the first real `Remap` consumer, so this is
  the first end-to-end validation of the JIT remap-evict path). The read-ROM/write-RAM split collapses to the
  read source per page on the single-backing page table — the cases DOS/ProDOS/CP/M use; the exotic
  simultaneous read-ROM-while-write-RAM page is scoped out (no target software needs it). No drift from PR-A's
  shipped `Remap` API. Gate: 12 LC tests (decode truth table + flip-flop + presence + peek-free + both-tier
  run-code) + the full 7121-test suite green. Unblocks PR-H (DOS lives in LC RAM) + PR-J (the Z80's
  `$B000`/`$D000` view reuses this `Remap`).
- **PR-D — `Apple2Keyboard` (`IKeyboardSink`) + `Apple2Speaker` (`IAudioSink`)** (2026-06-20). The ][+'s two
  host-facing chips over the shared `Apple2VideoState` the **already-shipped** IOU drives (no IOU/board/state
  API change — PR-H wires them into the surface). `Apple2KeyMap` folds the portable `KeyCode`/`Char` set to
  the ][+'s **uppercase-only** 7-bit codes (letters → `$41–$5A`; digits + symbols ASCII; Enter `$0D` / Space
  `$20` / Backspace `$08` / Escape `$1B`; a printable `Char` with no dedicated key falls back to its uppercase
  ASCII; everything else is a no-op). `Apple2Keyboard : IKeyboardSink` translates + `LatchKey` on key-**down**
  only (the ][+ latch has no release — it holds the last key until the guest reads `$C010`); key-up + unmapped
  keys leave the latch untouched. `Apple2Speaker : IAudioSink` resamples the IOU's monotonic `$C030` toggle
  **count** into S16 PCM (44100 / 1ch / 735-per-frame), reusing the `SpectrumUla` beeper-sink shape: spreads
  the frame's new toggles evenly, emits both polarities, and **carries** the ending level into the next frame;
  it reads-only (never mutates the shared state) and schedules a 60 Hz `AudioReady` tick in `Realize`. The
  un-fakeable gate runs on the **interpreter** (the oracle): a real 6502 `LDA $C030` loop on a built `Machine`
  toggles the speaker many times and renders a non-flat both-polarity frame — no faked toggles. Pre-merge
  review fix: the toggle index is `long` (an overflow guard against a saturated audio thread). Gate: 23 PR-D
  tests (keymap + keyboard + speaker + the interpreter-tier gate) + the full 7109-test suite green. Unblocks
  PR-H (surface wires the chips as the `IKeyboardSink`/`IAudioSink`).
- **PR-C — `Apple2Video` (`IDisplayDevice`): text / lo-res / hi-res render** (2026-06-20). One host-facing
  chip that reads **live main RAM** for scanout (no VRAM — the `SpectrumUla` pattern) and renders the ][+'s
  three modes into RGBA: text (40×24, GBASCALC interleave), lo-res (40×48 stacked nibble blocks), and hi-res
  (280×192). The hi-res `addr(y)` uses the **verified** two-level interleave (landmarks y=0→`$2000`,
  y=1→`$2400`, y=8→`$2080`, y=64→`$2028`, y=191→`$3FD0`; the refuted swapped-stride variant is excluded by a
  192-row bijection guard); page 2 reads `$4000`; text uses the GBASCALC row bases. Reads the shared
  `Apple2VideoState` the IOU writes, so a `$C057` HIRES access flips the next render with no plumbing. Ships
  correct mono + basic artifact + the 16-colour lo-res palette + a built-in fallback font (the real char-gen
  ROM injects in PR-H); `Realize` binds the live program space + schedules a 60 Hz `FrameReady` tick (no IRQ —
  the bare ][+ has no vblank). All render gates run on synthetic RAM, **no ROM**. Gate: 24 render/address tests
  + the full 7089-test suite green. Unblocks PR-H (surface + ROM-boot, which wires the chip in + injects the
  real char ROM).
- **PR-B — `Apple2Board` BoardSpec skeleton + `Apple2Iou` soft-switch decoder** (2026-06-20). The base
  ][+ as a declarative `BoardSpec` (48K RAM `$0000-$BFFF`, the `$C000-$CFFF` Mmio hole, 12K system ROM
  `$D000-$FFFF`, memory-mapped I/O only, reset-from-ROM-vector, no IRQ) + the `Apple2Iou` decoder owning
  the `$C000` page: the load-bearing ][+ rule — video/speaker/keyboard switches toggle on **any access**
  (read OR write, the IIe's inverse) via one shared `ApplyAnyAccessSideEffect`, while `TryPeek` is
  **peek-free** (the monitor can't change state by looking). The shared mutable `Apple2VideoState` is the
  one object the IOU writes and PR-C's video chip reads. Verified: a real `STA $C030` double-toggles the
  speaker (the cycle-exact `Mos6502Cpu` issues the NMOS RMW dummy read — no core gap). Gate: 23 Apple2
  tests + the full 7065-test suite green. Unblocks PR-C (video), PR-D (keyboard/speaker), PR-E (LC ports),
  PR-F (Disk II ports).
- **PR-A — `AddressSpace.Remap` seam + JIT invalidation listener** (2026-06-20). The run-time bank-switch
  primitive ADR 0009 Decision 2 designed: `Remap`/`RemapPeripheral` on `IAddressSpace` (in-place page-table
  re-point, memory↔MMIO), the `IMapInvalidationListener` seam (Core defines, Jit implements — AOT-clean),
  `BlockCache.InvalidatePages` (page-precise eviction), and `Fastmem.Reclassify`. Interpreter-correct on
  every access; the JIT re-classifies + evicts the remapped pages so the new bank's code runs. Inert until
  a device remaps (every existing board byte/cycle-identical). Unblocks PR-E (Language Card), PR-N (Videx
  `$C800`), PR-I (dual-CPU). Gate: 8 remap tests + the full 7042-test suite green.

The arc builds on the **shipped** SP0 web surface + the ZX Spectrum 48K machine (see `docs/ROADMAP.md`
§ *Recently shipped*), reusing the `BoardSpec`/`BoardMachineFactory`/`IPeripheral` + `IDisplayDevice` /
`IKeyboardSink` / `IAudioSink` / `IBlockDevice` contracts and the fetch-on-demand asset posture verbatim.
