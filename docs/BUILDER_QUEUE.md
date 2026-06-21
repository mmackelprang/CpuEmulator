# Builder Queue

> **Last updated:** 2026-06-20 (Builder — PR-B merged). **Owner:** Mark.
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
| **C** | `Apple2Video` (`IDisplayDevice`): text / lo-res / hi-res render | 📋 | B | [plan](superpowers/plans/2026-06-20-apple2-pr-c-video.md) | `RenderInto` reproduces the verified hi-res `addr(y)` landmarks (y=0→`$2000`, y=1→`$2400`, y=8→`$2080`, y=64→`$2028`, y=191→`$3FD0`) + the GBASCALC text row bases, reading live main RAM into RGBA. Synthetic RAM, no ROM. |
| **D** | `Apple2Keyboard` (`IKeyboardSink`) + `Apple2Speaker` (`IAudioSink`) | 📋 | B | JIT | `$C000` returns the latch (bit7 strobe + ][+ code), `$C010` clears strobe; `PostKey` folds to the uppercase-only ][+ set; `$C030` toggle log → S16 PCM both polarities + level-carry (the Spectrum beeper gate shape). |
| **E** | Language Card mapper (`$C080–$C08F`) — first `Remap` consumer | 📋 | A, B | JIT | Two consecutive odd-`$C08x` reads write-enable `$D000–$FFFF` RAM (one read does not); bank-1/bank-2 + read-ROM/read-RAM select correctly; each switch calls `Remap` and (JIT) evicts the banked pages; runs code out of LC RAM. |
| **F** | Disk II controller — `.woz`/LSS nibble path + `IFluxImage` seam | 📋 | B | JIT | The LSS sequencer produces the 6-and-2 GCR nibble stream a guest poll reads at `$C0EC`; stepper/motor soft switches drive head + the ~1 s 556 motor-off delay; `Fine` timing. The `IFluxImage` track-bitstream seam sits beside `IBlockDevice`. Synthetic `.woz` track, no ROM. |
| **G** | Disk II — `.dsk`/`.po` re-nibblizing adapter | 📋 | F | JIT | A `.dsk`/`.po` logical-sector image re-nibblizes into a synthetic track on the **same** `IFluxImage` path PR-F reads — the controller is format-agnostic above the seam. Synthetic `.dsk`, no ROM. |
| **H** | `Apple2Surface` + `get-apple2-roms.{sh,ps1}` + ROM-boot gate | 📋 | C, D, E, F, G | JIT | With the system + char-gen ROMs fetched, the ][+ boots to the Applesoft `]` prompt (text-screen RGBA assertion) on **both** tiers; DOS 3.3 boots from a `.dsk` in drive 1. **Asset-gated** (skip-with-note absent). |
| **I** | Dual-CPU `Machine` / `MachineBuilder` scaffolding (`CoprocessorSpec`) | 📋 | A | JIT | `CoprocessorSpec` + `WithCoprocessor` + the dual-CPU `Run` build a 2-CPU machine; the **single-CPU path is byte-for-byte unchanged** (every existing board regression-identical); all interrupts route to the primary 6502; the dormant core is never scheduled. |
| **J** | `SoftCardTranslation` (6-branch table) + `TranslatingAddressSpace` + `SoftCardControlPort` | 📋 | I | JIT | All **6** translation branches assert at their boundaries (`$AFFF→$BFFF`, `$B000→$D000`, `$EFFF→$CFFF`, `$F000→$0000`, …) — the refuted `+$1000 mod 64K` shortcut fails branch 2–6; the control-port write flips `_z80Active` and ends the slice. |
| **K** | Interpreter-tier CP/M boot wiring (`$C600`→tracks→`$CnXX`-start) | 📋 | E, F, H, J | JIT | The real SoftCard boot sequence (6502 `$C600` reads tracks `$00–$02`, sets LC banking, writes `$CN00`) hands off to the Z80; CP/M reaches its load state on the **interpreter** tier. **Asset-gated** on the SoftCard CP/M `.dsk` (skip-with-note absent). |
| **L** | JIT-under-translation (pre-translated physical fastmem) | ⏸️ | K | JIT | *(deferred/optional, ADR 0015 Decision 4 — measure interpreter CP/M throughput first.)* The Z80-under-translation gets fastmem over the physical backing arrays; parity-gated against the running interpreter SoftCard (the oracle). |
| **M** | `DisplayMultiplexer` + `MachineHost` per-frame re-size | 📋 | — | JIT | The multiplexer delegates `Width`/`Height`/`RenderInto`/`FrameReady` to the active source; `SetActive` fires `FrameReady`; `MachineHost` re-sizes its `_rgba` buffer when dimensions change; a single-display board is transparent (no behavior change). |
| **N** | `VidexVideoterm` (`IPeripheral`+`IDisplayDevice`+`IFastMemoryProvider`) + `$C800` expansion-bank mapper | 📋 | A, M | JIT | The 6845 CRTC programmed via `$C0B0`/`$C0B1` + the `$C8A1` init table yields 80×24; VRAM+charROM → RGBA; the `$C800` mapper (2nd `Remap` consumer) banks the firmware window + the `$CC00–$CDFF` VRAM bank; the enable signal calls `SetActive`. Synthetic char ROM, no asset. |
| **O** | Videx + CP/M asset scripts (`get-videx-roms`, `get-softcard-cpm`) + CP/M-on-Videx end-to-end gate | 📋 | K, N | JIT | With all assets fetched, inserting + booting the CP/M disk widens the display to the **80-col Videx terminal** and reaches the `A>` prompt on **both** tiers. **Asset-gated + owner-sign-off-given** (skip-with-note absent). |
| **P** | The `ST` status-frame seam (host→client read-only indicators) | 📋 | — | JIT | A new lightweight `ST` wire frame carries board name, asset state, per-drive motor + image label, video-mode label; the client renders them read-only; the host pushes real machine state (not faked). *(Designer T-A — suggested early; most surface indicators consume it.)* |
| **Q** | In-session disk insert / eject mechanism (Disk II runtime image swap) | 📋 | F, G | JIT | The Disk II controller accepts "load these bytes as drive N's image" + "eject drive N" at runtime, for both `.woz` and `.dsk`/`.po`, via the `IFluxImage` seam; a running machine swaps images without rebuild. *(Designer T-D — shared dep of the two disk-UX paths.)* |
| **R** | `GET /disks` catalog endpoint + per-drive library dropdown | 📋 | Q | JIT | The server lists the cached `disks/` images (name, format, drive-compat, CP/M grouping); both per-drive `[ Library ▾]` selects populate from it; an empty catalog disables the select with the named-script hint. *(Designer T-C.)* |
| **S** | Disk-upload inbound-binary path (the NEW binary WS frame + validation + UPLOADING state) | 📋 | Q | JIT | Client `<input type=file>` → client validation (ext / 2 MB cap / non-empty) → binary WS `DK` frame → **server** re-validation (`.woz` magic / `.dsk`/`.po` exact length) → load into drive N; the UPLOADING → INSERTED / error states drive the panel. *(Designer T-B — the surface's first inbound binary path; explicitly its own task.)* |
| **T** | Control-strip UI (drive panels, lights, mode label, asset banner) | 📋 | P, R, S | JIT | Two bordered drive panels (library select + upload + eject + a real-motor amber light driven by `$C0E8/$C0E9` + the 1 s off-delay, **not** faked on insert); the calm named-script asset banner replaces the silent fallback; one new `--drive-active` token. *(Designer T-E/T-G/T-H + keyboard extensions T-F.)* |

---

## Per-row notes, dependencies, and just-in-time planning

**Planned now (ready for Builder):** **A, B, C** have detailed bite-sized plans
(`docs/superpowers/plans/2026-06-20-apple2-pr-{a,b,c}-*.md`). These unblock the foundation immediately.

**Planned just-in-time (`Plan: JIT` above):** D–T are queued with their dependencies + un-fakeable gate
fixed, but their bite-sized plans are written **as each approaches the front of the queue** (the
established cadence — the Spectrum/M6 arcs planned in waves, not all at once). When a `JIT` row becomes
the topmost eligible item, Builder stops and asks Planner for the detailed plan. This keeps each plan
grounded against the *then-current* `main` (e.g. PR-E's plan is written after PR-A has actually landed
the `Remap` API, so its literal code calls the real shipped signature).

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
