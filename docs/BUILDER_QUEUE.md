# Builder Queue

> **Last updated:** 2026-06-21 (Planner — **SoftCard CP/M boot-to-`A>` fix arc PLANNED (new rows CPM-1…CPM-4)**
> — grounded against `main` @ `1d0232c` (the commit carrying ADR 0017). The Architect root-caused the never-booting
> CP/M deliverable to a **three-defect cascade** (live-verified on the real disk) and decomposed the fix; these four
> bite-sized TDD plans execute PRs 1–4 (PR-5 Videx 40-col re-frame is owner-gated, **not** in this batch).
> **Live-verified current state:** `main` @ `1d0232c` is **2-failed** on a machine with the CP/M assets cached
> (`~/.cache/cpuemulator/vectors/cpm/softcard-cpm.dsk`) — `SoftCardBoardTests.Cpm_boots_to_the_A_prompt_on_the_interpreter`
> fails at `CoprocessorActive` and `SoftCardVidexBoardTests.Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter`
> fails at `ActiveIndex==1` (got 0). These are the **same 2 failures PR #128's Test Plan documents** — **CPM-1
> restores GREEN/HONEST main and unblocks PR #128's clean merge.**
> **CPM-1** ([plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr1.md), no deps): per-track CP/M skew (boot
> table `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]` for system tracks 0–2, data table for 3+; `Apple2SectorOrder.(kind,track)`
> overload + `DskFluxImage` per-track resolution) + de-fang **both** CP/M boot gates (honest negative assertion +
> named-skip of the `A>` part) so the suite is green and can't false-pass. **CPM-2**
> ([plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr2.md), dep CPM-1): `SoftCardControlPort.Read()` open-bus,
> toggle on write only (kills the `CAN'T FIND Z80 SOFTCARD` livelock). **CPM-3**
> ([plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr3.md), dep CPM-2): `RunDualCpu` drives the active core by
> `Step()` and yields at the `$CnXX` write — single-CPU path **byte-for-byte unchanged**. **CPM-4**
> ([plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr4.md), dep CPM-3): the live `A>` deliverable — decode the
> 40-col text page and assert the real `A>` / CP/M sign-on substring + `CoprocessorActive` + capture the real frame hash
> (replace PLACEHOLDER); close any `$1010` 6502-BIOS↔Z80 bridge residual by **live triage** (Decision 4 — may be a no-op).
> **Strictly ordered CPM-1 → CPM-2 → CPM-3 → CPM-4** (each fix advances the live boot one verified stage; the live
> disk is every PR's un-fakeable gate; the on-screen `A>` is the final arbiter). **Three drifts from ADR 0017 flagged in
> the plans:** (1) CPM-1 also de-fangs the **Videx** `[SoftCardCpmFact]` gate (ADR names only the SoftCard one, but the
> Videx gate is the 2nd RED gate — leaving it red means main isn't green; named-skipped until the owner-gated PR-5);
> (2) the `DskFluxImage` change is a field-type change (store `SectorOrderKind`, resolve per track), not the ADR's
> "one-line" claim; (3) CPM-4's `A>` gate is on the 40-col SoftCard board (`Apple2Video`, no multiplexer), so the
> `ActiveIndex==0` assertion the ADR mentions belongs to the Videx gate (PR-5), not CPM-4. **All four are Builder-ready;
> CPM-1 has no deps and is the topmost. Next: Builder picks up CPM-1** — it restores green main first. PR-5 (Videx
> 40-col re-frame, ADR 0017 Decision 6) is **owner-gated on an 80-col CP/M master and intentionally NOT queued here.**)
> (Planner — **ZX Spectrum 48K ROM UAT PLANNED (new row SU)** — grounded against
> `main` @ `fbd3a61`. A live diagnostic found the shipped Spectrum boot gate
> (`tests/CpuEmulator.Tests/Spectrum/SpectrumBootTests.cs`) has `BootCycles = 200_000` **~30× too small** (the
> real 48K power-on RAM test isn't even done; full boot to the © screen ≈ **5.9M** T-states, stable by ~13M) +
> a wrong "≈140k/two frames" comment. The plan
> ([plan](superpowers/plans/2026-06-21-spectrum-48k-rom-uat.md)) **(1)** recalibrates `BootCycles`→**7M** + fixes
> the comment; **(2)** turns the boot gate into a `[Theory]` over **(variant × tier)** across the owner's six
> 48K ROMs (canonical `spec48` + arabic-v1/v2/v31 + beckman + prototype, each 16384 B) on Interpreter AND Jit —
> variant-safe structural assertion (mostly-white `Colors[7]`=`0xFFD7D7D7` paper + black `Colors[0]`=`0xFF000000`
> ink, floor `>50`, canonical `>200`) + a per-variant committed RGBA hash (both tiers byte-identical at
> completion); a new `SpectrumRomVariants.Discover` enumerates present `<cache>/spectrum/variants/<name>.rom`
> ROMs (skip-with-note when none) + `get-spectrum-rom-variants.{sh,ps1}` (owner-copy from
> `D:/prj/zx-roms/spectrum16-48/`, never vendored); **(3)** adds an interactive BASIC UAT (canonical ROM, both
> tiers) — boot to the `K` cursor, drive the key matrix to type **`PRINT 2+2`+ENTER** (keyword `P`→`PRINT`;
> `SymbolShift`+`K`=`+` chord; `Enter`) and assert the printed `4` appears in the top print rows (ink-delta +
> committed hash), proving boot → keyboard → BASIC interpreter → screen end-to-end. **48K-only** (128/+2/+3 are a
> separate future arc). Two shipped-API facts grounded: `SpectrumRom.TryGetPath()` gains an additive optional-
> `root` overload (no-arg call sites unchanged); the project is **xUnit v2.9.3** (no `Assert.SkipWhen` — the
> `[MemberData]`-empty hazard is handled with an attribute-level `Skip` + sentinel row + early-return). Notes
> (NOT built): a `--board` surface override (the server probes Apple before Spectrum) + the Tester scratch
> cleanup (`tools/SpectrumProbe/`, `tools/WsProbe/`, `.uat-artifacts/`). **SU is Builder-ready (no deps).
> Next: Builder picks up SU** — runs the variant-copy script, implements Task 1→2→3 TDD, captures the
> per-variant + interactive hashes on first green run.) (Builder — **APPLE ][+ BOOT NOW LIVE-VERIFIED — test-only boot-gate
> recalibration (post-arc fix cycle)**. An owner live-UAT of the Apple ][+ web surface against a real
> owner-supplied 12 KiB Apple system ROM found that the H-row boot gate
> (`Apple2BootTests.Rom_boots_to_the_applesoft_prompt_on_both_tiers`) built the **WRONG board** — the
> emulator itself is correct (the live surface cold-boots cleanly, interactively: `PRINT 2+2`→`4`). The
> gate used `SpecWithSystem` + a **fake 256-byte slot-6 boot ROM**, so the Autostart scan `JMP ($C600)`s
> into non-functional bytes → `BRK` → it landed in the **Monitor `*` prompt** (40 ink px), failing its own
> `onPixels > 50` assertion AND contradicting its name. The live surface uses `SpecWithDiskII` (no `$C600`
> window) and correctly falls through to a real BASIC prompt (186 ink px). **Fix 1 (HIGH, test-only):**
> rebuilt the gate's board to `Apple2Board.SpecWithDiskII` (the exact live-surface path), made the
> assertion **ROM-agnostic + structural** (mostly-blank text screen + an ink floor `onPixels > 100` — safely
> between the Monitor-`*`/dead-board failure cases [≤40] and the real boot [186], so it holds for either
> BASIC ROM: Integer `>` OR Applesoft `]`), **dropped the committed-RGBA-hash gate** (it would falsely fail
> the other ROM — the structural floor is the robust gate), dropped the "heading" wording, and **renamed**
> it `Rom_boots_to_a_basic_prompt_on_both_tiers`. The owner's cached ROM is an **Integer-BASIC Autostart
> dump**, so the live boot lands at the Integer `>` prompt — CORRECT and faithful (a ][+ Applesoft ROM
> would show `]`). The gate **runs live on both tiers** with the ROM cached (interpreter + JIT, GREEN) —
> the skip count dropped 6→5. **Fix 2 (LOW, operator):** the published web-surface DLL defaulted its content
> root to the CWD, so launching from the repo root 404'd at `/` (`WebRootPath not found`); `Program.cs` now
> sets `ContentRootPath = AppContext.BaseDirectory` (an explicit `--contentRoot` still wins; the
> `WebApplicationFactory<Program>` smoke tests are unaffected — the factory overrides the content root after
> `Main`). **UAT-verified:** published DLL launched from the repo root → `GET /`+`/app.js`+`/index.html`
> 200, `GET /disks` 200 `[]`, content root resolved to the DLL dir. **Fix 3 (cleanup):** removed the
> Tester's scratch browser drivers (`tools/uat-apple2-{boot,interact}.mjs`); **kept** `tools/BootProbe/` (a
> clean, warning-free headless boot-triage dev tool — boots both board configs, dumps text page + ink count
> + frame hash; it derived the 100 floor) and added it to the `.slnx` under `/tools/`. **Pre-merge review:
> 0 HIGH, 0 MEDIUM, 1 LOW (fixed** — a stale `> 50` threshold string in BootProbe's console dump). Full
> suite **7281 passed / 0 failed / 5 skipped**, warning-clean (Release, whole solution). **STOP per protocol
> — this was a directed post-arc fix cycle, not a queue row.** What remains in the Apple ][+ space is
> unchanged: backlog row **W** (`WozFluxImage`) + deferred row **L** (JIT-under-translation) + the owner-
> browser-UAT confirmations (80-col CP/M render; panels/amber-light/`Ctrl+B` in a browser).) (Builder — **T SHIPPED (PR #125) — the Apple ][+ planned arc (A–T) is
> STRUCTURALLY COMPLETE**: the control-strip UI (two bordered drive panels — library select + upload picker
> + eject + a **real-motor amber light** [`st.drives[i].motor` = the REAL `$C0E8/$C0E9` line + the ~1 s 556
> off-delay, never faked on insert] + the current-image label), the calm named-script asset banner (the
> shipped `applyAssetBanner` kept verbatim, never red), the read-only video-mode label (`st.mode`), and the
> **one** new `--drive-active` (`#d8a657`) token — all built in `app.js`+`index.html` from the shipped
> Spectrum literals, binding to the P/R/S seams (`window.machineStatus`/`diskCatalog`/`insertFromLibrary`/
> `ejectDrive`/`uploadDisk`/`uploadState`). **PLUS D5** (the T-F ctrl-wiring, additive across 4 shipped
> files): `KeyEvent` went 3-arg→**4-arg defaulted** (`bool Ctrl = false` — all 18 shipped 3-arg call sites
> compile unchanged, verified), `Apple2KeyMap.TryMap` ANDs a letter/printable with `$1F` on ctrl,
> `Apple2Keyboard.PostKey` passes `e.Ctrl`, `FrameCodec.TryDecodeKey` reads the optional `ctrl` JSON field,
> and `app.js` `sendKey` sends `ctrl: ev.ctrlKey` + `preventDefault`s `Ctrl+B`/`Ctrl+C`. **No `KeyCode` enum
> addition.** **Pre-merge review: 0 HIGH, 0 MEDIUM, 1 LOW (deferred, justified** — the printable-char arm
> folds `$1F` for non-letter printables too; the plan deliberately scopes the fold to letters/printables and
> no Applesoft/DOS chord uses Ctrl+symbol — out of scope for T). **Hybrid gate all green:** (a) the served-
> asset content gate (`ControlStripAssetTests` via `WebApplicationFactory<WebProgram>` — the panel DOM /
> `--drive-active: #d8a657` / the control wiring / the `ctrl` send present in `/app.js`+`/index.html`); (b)
> the shipped R/S/P wire-seam gates (still green); (c) **the D5 un-fakeable interpreter gate**
> (`Apple2CtrlKeyTests`: a `Ctrl+B` `KeyEvent` posted to the real `Apple2Keyboard` latches **`$02`** [not
> `$42`] at `$C000`; `Ctrl+C`→`$03`; plain `B`→`$42`) + the wire decode (`KeyEventCtrlDecodeTests`). Full
> suite **7279 passed / 0 failed / 6 skipped** (+12 net new), warning-clean. **UAT** (live out-of-process
> server, ROM-absent demo branch): `GET /`+`/app.js`+`/index.html` 200 with all control-strip + D5 markers
> served; `GET /disks` → `[]` (empty-catalog path); a real `ClientWebSocket` session led with `ST demo`,
> streamed 148 `FB` frames, then **a genuine `Ctrl+B` keydown JSON carrying the new `ctrl:true` field was
> accepted with zero server errors** + the session stayed healthy (126 more `FB` frames) → clean
> NormalClosure. The **in-browser visual confirmation is owner UAT** (panels render, the amber light lingers
> ~1 s on real disk access, a real `Ctrl+B` drops to BASIC) — needs a browser + cached Apple ROMs.
> **STOP per protocol — T was the FINAL planned row.** What remains in the Apple ][+ space: backlog row **W**
> (`WozFluxImage`, the `.woz`-file byte parser — `JIT`-unplanned, do not author) + deferred row **L**
> (JIT-under-translation) + the owner-asset / owner-browser-UAT confirmations. **The Planner reconciles next
> (W/L or a new arc).**) (Planner — **FINAL surface-UI row T PLANNED** — the control strip + keyboard
> T-F. [plan](superpowers/plans/2026-06-20-apple2-pr-t-control-strip.md), grounded against `main` @ `f4755e5`
> (PRs #99–#123). **T-F scope = (B), full, including D5** (the `ctrl`-modifier wiring) per the Coordinator's
> decision — the hint line already advertises "Ctrl+B = BASIC", so shipping it unwired would be a lying
> affordance below this surface's honesty bar. T builds the visible client DOM that binds to the already-
> shipped P/R/S seams (the `ST` `window.machineStatus`, R's `window.diskCatalog`/`insertFromLibrary`/
> `ejectDrive`, S's `window.uploadDisk`/`uploadState` + the `st.upload` ack route) — **two bordered drive
> panels** (library `<select>` from `GET /disks`; `Insert…` upload picker → `DK` with UPLOADING→INSERTED/
> error; eject; the current-image label, both drives; a **real-motor amber light** from `st.drives[i].motor`
> = the REAL `$C0E8/$C0E9` line + ~1 s 556 off-delay, single shared motor line, NOT faked on insert), the
> **calm named-script asset banner** (the shipped `applyAssetBanner` kept verbatim; never red), the read-only
> **mode label** (`st.mode`), and the **one** new token `--drive-active` (amber `#d8a657`) — everything else
> reuses the shipped Spectrum literals. **PLUS the D5 chip+wire change** (touches server+chip, intended): a
> trailing `bool Ctrl = false` on `KeyEvent` (non-breaking — all 8 shipped 3-arg call sites compile
> unchanged), `FrameCodec.TryDecodeKey` reads the optional `ctrl` JSON field, `Apple2KeyMap.TryMap` ANDs a
> letter with `$1F` when `ctrl` is set, `Apple2Keyboard.PostKey` passes `e.Ctrl`, and `app.js` `sendKey`
> adds `ctrl: ev.ctrlKey` + `preventDefault`s `Ctrl+B`/`Ctrl+C`. **Hybrid gate, no new JS toolchain:** (a) C#
> served-asset content assertions (`WebApplicationFactory` reads `/app.js`+`/index.html` — the panel DOM /
> the `--drive-active` token / the control wiring / the `ctrl` send are present); (b) the shipped wire/seam
> gates (R/S/P, already green); (c) the **D5 un-fakeable interpreter gate** — a `Ctrl+B` `KeyEvent` posted to
> a real `Apple2Keyboard` latches `$02` (not `$42`) at `$C000` over the live latch, `Ctrl+C` → `$03`, a plain
> `B` → `$42`. The **in-browser visual confirmation** (panels render, the amber light turns on during a real
> disk access + lingers ~1 s, the dropdown populates, a real `Ctrl+B` drops into BASIC) is explicitly **owner
> UAT** — the surface stays a dumb reflector, no indicator client-fabricated. **No `KeyCode` enum addition**
> (Ctrl rides the `Ctrl` field, design §2.5). `WozFluxImage` (row W) stays backlog — `.woz` library items
> render disabled-with-note, uploads honestly reject. **T is Builder-ready** — it is the topmost (and final)
> eligible surface-UI row; deps P/R/S all ✅. **W remains a backlog `JIT` row; L is deferred. With T cleared,
> the Apple ][+ surface arc is complete.** **Next: Builder picks up T.**) (Builder — **S SHIPPED (PR #123)**: the disk-upload inbound-binary path (the
> surface's FIRST inbound binary WS message). `FrameCodec.TryDecodeUpload`/`UploadFrame` decode the binary
> **`DK`** frame (`'D','K',version,drive,formatByte,...bytes`); `UploadValidator` re-validates server-side
> (`.dsk`/`.po` exactly `DiskImageFactory.DskBytes`=143360; `.woz` magic `WOZ1`/`WOZ2` then the **honest
> not-yet-supported reject** — never reaches `insertDisk`); `EncodeUploadAck` pushes an `ST`-prefixed
> `{"upload":{drive,ok,message}}` text frame. **The receive loop reassembles the multi-fragment message**
> (the load-bearing detail — a `.dsk` is 143360 bytes, far over the buffer [grown 1 KiB→8 KiB]; accumulate to
> `EndOfMessage`, 2 MiB cap) → validate → load via the shipped R/Q `insertDisk` delegate → ack. Client gains
> `window.uploadDisk(drive,file)` (ext/2 MB/non-empty validation → `FileReader` → binary send) +
> `window.uploadState`/`uploadLastError` + the `st.upload` ack route (no panel DOM — row T). The single-text-
> frame protocol is unaffected (R's HIGH try/catch preserved through the receive-loop rewrite). **Pre-merge
> review: 0 HIGH, 2 MEDIUM + 1 LOW (all fixed).** M1: an oversized multi-fragment message reset the
> accumulator on the cap-fire fragment then re-accumulated the tail + dispatched a partial frame (wrong
> "corrupt" ack) — added a `capExceeded` drain flag (ack "too large" once at `EndOfMessage`). M2: a valid
> `.dsk` on a no-Apple-drive session (`insertDisk` null) acked "corrupt" — now "Disk upload isn't supported
> in this session". L1: the client ext-parse reached the format map by accident on a no-dot filename —
> guarded the `-1` case. Full suite **7267 passed / 0 failed / 6 skipped** (+16 net new), warning-clean,
> stable across runs. **UAT** (live out-of-process server, ROM-absent): a real `ClientWebSocket` sent a
> genuine **143365-byte `DK` frame** (fragments over the wire) → server reassembled + validated + acked
> (`ok:false, "Disk upload isn't supported in this session"` — demo board, exercising M2 live + proving
> multi-fragment reassembly end-to-end); a 100-byte `DK` → `ok:false, "That image looks corrupt"`; a key
> event after the binary burst → 3 FB frames stream (text path healthy); zero server errors. **STOP per
> protocol — R + S cleared.** The next eligible row is **T** (control-strip UI, deps P/R/S ✅) but it is
> **`JIT`-unplanned**; **W** is a backlog `JIT` row; **L** is deferred. **The Planner plans T (the final
> surface-UI row) next.**) (Builder — **R SHIPPED (PR #122)**: `GET /disks` disk-library catalog
> (`DiskCatalog` over `<cache>/disks/` + the CP/M `.dsk`, deterministic, `.woz` listed-disabled) + the
> `disk-insert`/`disk-eject` text-WS dispatch (a library selection resolves the id server-side, reads the
> cached bytes, inserts via the shipped Q `surface.InsertDisk`) + the **drive-2 status fold-in** (the `ST`
> frame now reports BOTH drives via a mutable `DriveLabels` holder + the four-arg `InsertDisk(…,label)`; the
> two-arg overload kept). Client gains read-only `loadCatalog`/`window.diskCatalog` + `insertFromLibrary`/
> `ejectDrive` text senders (no panel DOM — row T). **Pre-merge review: 1 HIGH (fixed)** — the insert branch
> called `File.ReadAllBytes`+`FromBytes` unguarded, so a vanished/truncated library file (TOCTOU /
> non-256-multiple length) would throw out of `ReceiveKeysAsync` and tear down the live WS session; wrapped
> in a try/catch (a bad disk is now a clean no-op). **1 MEDIUM (fixed)** — `DriveLabels` cross-thread fields
> made `volatile`. **Deferred (justified):** M1 (multi-fragment WS text reassembly — pre-existing on the key
> path; lands in **S**'s receive-loop rewrite); L1 (`TryDecodeKey` accepts non-key JSON — correct given the
> documented disk-before-key ordering). **Test-isolation fix:** the plan's literal gate test mutated the
> process-global `CPUEMULATOR_TESTVECTORS` (the parallel TomHarte/Klaus vector suites read it live → flaky
> cross-suite failures); rewrote it onto the `DiskCatalog.List(root)`/`TryResolve(root)` seam the plan
> documents — no process-global mutation. Full suite **7251 passed / 0 failed / 6 skipped** (+12 net new),
> warning-clean, stable across consecutive runs. **UAT** (live out-of-process server, ROM-absent): `GET /`
> 200, `GET /app.js` 200 (carries all four R transport helpers), `GET /disks` 200 → `[]` (empty-catalog
> path); a real WS session leads with `ST demo`, then a `disk-insert` (non-existent id — the H1 path) +
> `disk-eject` + key-A burst, after which 5 binary `FB` frames stream at 256×192/196616 B — session healthy,
> zero server errors. **Next: S** (the upload binary path, deps Q ✅ — best after R, reuses R's `insertDisk`
> hoist + four-arg insert + the drive-2 fold-in).) (Planner — **surface-UI batch R + S PLANNED** (the disk-library catalog +
> the upload binary path, the two now-eligible surface-arc rows), grounded against `main` @ `204cf3d`
> (PRs #99–#120). **R** ([plan](superpowers/plans/2026-06-20-apple2-pr-r-disk-library.md), deps Q ✅): a new
> `DiskCatalog` (in `CpuEmulator.Machines`, beside `Apple2Rom`/`SoftCardCpm`) enumerates `<cache>/disks/*.dsk|
> *.po|*.woz` + the already-cached CP/M `.dsk` (`<cache>/cpm/softcard-cpm.dsk`, grouped last + flagged) into
> deterministic `DiskCatalogEntry`s; `Program.cs` maps **`GET /disks`** (compact JSON) + threads two hoisted
> delegates (`insertDisk`/`ejectDisk`) from the chosen Apple surface into `ReceiveKeysAsync`, which now also
> dispatches a **`disk-insert`/`disk-eject` text** message (new `FrameCodec.TryDecodeDisk`) — a library insert
> resolves the id → reads the cached bytes server-side → calls the shipped **Q `surface.InsertDisk`**. `.woz`
> entries are listed **`supported:false`** (no `WozFluxImage` yet) and never inserted (the dispatch guards
> `format != Woz`). **Folds in the PR-Q drive-2 deferral:** a tiny mutable `DriveLabels` holder on each of the
> three surfaces grows `Status()` to **two `DriveStatus` entries** (both report the shared `Disk.MotorOn` motor
> line — correct for the one-motor Disk II; only labels are per-drive), updated on insert/eject; the four-arg
> `InsertDisk(drive,bytes,format,label)` is added (the two-arg PR-Q overload kept). The client gains read-only
> `loadCatalog()`/`window.diskCatalog` + `insertFromLibrary`/`ejectDrive` text senders (the visible `[ Library
> ▾]` panel is row T, which binds to these). Gate (in-memory `WebApplicationFactory`, seeded cache): `GET
> /disks` lists a seeded dir + a `disk-insert` keeps the live session streaming + a resolved-then-loaded `.dsk`
> reads back a real nibble off the `$C0E9`/`$C0EC` bus (un-fakeable). **S** ([plan](superpowers/plans/2026-06-20-apple2-pr-s-disk-upload.md),
> deps Q ✅; best after R): the surface's **first inbound binary path** — a client `<input type=file>` →
> client validation (ext allow-list / 2 MB cap / non-empty) → a binary **`DK` frame** (`'D','K',version,drive,
> formatByte,...bytes`, `FrameCodec.TryDecodeUpload`) → **server re-validation** (`UploadValidator`: `.dsk`/`.po`
> exactly `DiskImageFactory.DskBytes`=143360; `.woz` magic `WOZ1`/`WOZ2` then the **honest not-yet-supported
> reject** — no `WozFluxImage`) → load into drive N via the **Q `insertDisk`** delegate (reused from R). The
> receive loop (which today **drops every binary frame**) gains a binary branch that **reassembles the
> multi-fragment** message (a `.dsk` is 143360 bytes — far over the receive buffer; the load-bearing detail) up
> to a 2 MiB cap, then validates + dispatches + pushes an **upload-result ack** (`EncodeUploadAck` — an `ST`-
> prefixed `{"upload":{drive,ok,message}}` text frame the client routes to resolve UPLOADING → INSERTED/error).
> The client gains `uploadDisk(drive,file)` + `window.uploadState`. Gate: a valid `.dsk` `DK` frame inserts +
> ack `ok:true`; a wrong-length `DK` → ack `That image looks corrupt`; a key event still streams FB after the
> binary branch lands (the single-text-frame protocol unaffected). **Two shipped-API facts grounded:** (1) the
> receive loop's `ReceiveKeysAsync(socket, pump, ct)` only handled **text** keys (binary `continue`d) — R/S
> thread `insertDisk`/`ejectDisk`/`pushText` through it; (2) `app.js` already carries the PR-P structured-`ST`
> decoder + `window.machineStatus` + `ws.binaryType="arraybuffer"` — R/S add transport helpers only (no panel
> DOM — row T). **Backlog row W added:** **`WozFluxImage`** (a thin `.woz`-file byte parser → `IFluxImage`, deps
> F ✅) — the missing half of the locked "full `.woz` fidelity upfront" decision (PR-F shipped the read path +
> seam, no file parser); `JIT`-unplanned (separable). **Both R + S Builder-ready** — R (deps Q ✅) is the
> topmost eligible row; S follows R. **T stays `JIT`** (deps P/R/S — planned after R/S ship). **Next: Builder
> picks up R.**) (Builder — **Q SHIPPED (PR #120)**: in-session Disk II insert/eject (the
> runtime image swap, design T-D — the shared dep of R/S). The controller's single `_image` becomes a
> 1-based `IFluxImage?[3] _drives`; the read routes through the **selected** drive (`_drives[_drive]`), so
> 1-based `IFluxImage?[3] _drives`; the read routes through the **selected** drive (`_drives[_drive]`), so
> `$C0EB`-select / **drive 2 is real for the first time** (tracked-but-ignored before Q). Runtime
> `Insert(int,IFluxImage)` / `Eject(int)` / `HasImage(int)` re-seek the active head on a swap, range-guard
> drives 1–2, and an empty drive reads **nothing** (bit 7 never sets). A shared `DiskImageFactory.FromBytes`
> + `DiskFormat{Woz,Dsk,Po}` build the `IFluxImage` R/S both call (`.dsk`→Dos33, `.po`→ProDos; raw **`.woz`
> bytes throw an explicit `NotSupportedException`** — no `.woz`-file parser shipped, a separable
> `WozFluxImage` follow-on; `.woz` **at the seam** inserts identically, proven with `SyntheticFluxImage`).
> Surface `InsertDisk(drive,bytes,format)` / `EjectDisk(drive)` on all three Apple surfaces (the `Disk` field
> from PR-P) — the R/S **call point**; Q adds **no** WS message handler (R = library text, S = binary `DK`
> upload). The single-image constructor is **unchanged** — every shipped surface/test builds drive 1 as
> before (`_image` fully removed; the byte-for-byte regression gate). **Pre-merge review: NO HIGH bugs** —
> the one implementer deviation (`if (_bitPos >= bitLen) _bitPos %= bitLen;` in `ReadDataLatch`) was confirmed
> **correct + necessary + correctly placed** (a `$C0EB` drive-select, which doesn't reset `_bitPos`, can
> leave the head past a shorter selected track's bitstream — a real OOB the plan's literal omitted; a no-op
> on the single-drive path, after the `bitLen<=0` guard). The stepper clamp `?? 1` fallback, range guards,
> empty-drive read, and regression identity all clean — **no fixer needed.** **Un-fakeable gates**
> (interpreter, synthetic images, headless): a running machine `InsertDisk(1,dskBytes,Dsk)` reads a real GCR
> nibble off the runtime-inserted image via the live `$C0E9`/`$C0EC` bus; after `EjectDisk(1)` 50k polls
> latch nothing; the read follows the selected drive (distinct drive-2 image); a runtime `.dsk` decodes to a
> known 256-byte track-0 sector. Full suite **7239 passed / 0 failed / 6 skipped** (+7 new), warning-clean.
> **UAT** (no browser MCP; HTTP+WS frame level — Q touches the shared disk hot path): `GET /`+`/app.js` 200,
> the `ST demo` text frame leads, 115 binary `FB` frames stream at 196616 bytes, zero server errors. **One
> recorded deferral (Planner):** `Status()` (the PR-P `ST` frame) still reports a **single** drive entry
> though drive 2 is now real — a drive-2 indicator needs a `Drive2Label` threaded through `InsertDisk(2,…)`,
> genuine **R/S/T** territory (the two-drive panel + per-drive label tracking live there, consuming this
> frame); the codec already carries an N-element `Drives` array, so R/S/T grow the 2nd `DriveStatus` with no
> Q change. **STOP per protocol:** with Q ✅, **R** (`GET /disks` + library dropdown) + **S** (upload binary
> path) become eligible (deps Q ✅) but are **`JIT`-unplanned**; **T** depends on P/R/S; **L** is deferred →
> Builder stops; the **Planner plans R + S next** (grounded against shipped P/Q). Builder — **P SHIPPED (PR
> #119)**: the `ST` status-frame seam. The host
> pushes the REAL machine state — board name + asset state + the live video-mode label (`Apple2Video.ModeLabel`,
> derived from the same `Apple2VideoState` flags the renderer reads) + per-drive **real** motor
> (`Apple2DiskII.MotorOn` — the `$C0E8/$C0E9` switch + the shipped ~1 s 556 off-delay, **false at boot**, NOT
> faked on insert) + image label — as a **structured `ST` text frame** (the wire tag `ST` reused as `"ST " +`
> compact deterministic JSON; the binary FB/AU path untouched; the Spectrum/demo legacy one-shot `ST <asset>`
> text frame preserved so `WebServerSmokeTests` stays green). A session-level **`StatusPusher`** pushes on the
> pump tick **only when the snapshot changes** (byte-compare on the deterministic JSON; first tick always
> pushes the boot state); **`app.js`** decodes it read-only + exposes `window.machineStatus` for row T. The
> `Apple2DiskII Disk` + `Drive1Label` fields were added to the three Apple surface records (the **P/Q
> coordination field — P added it, Q reuses it**). **Pre-merge review: NO HIGH findings** (the legacy-path
> backward-compat, the real motor flag, the pump-tick wiring + teardown, the deterministic-encoding
> change-detection, the private-method rename completeness, the additive record shape, and the ModeLabel
> precedence all clean); 2 MEDIUM were plan-intentional (the LORES+MIXED label collapse per the "Mixed wins"
> spec; the pre-overwrite `applyAssetBanner` DOM write), 2 LOW non-issues — **no fixer needed.** **Un-fakeable
> gates** (headless, asset-free): the real `$C0E9` motor switch through the live bus flips
> `Status().Drives[0].MotorOn` (false at boot — un-fakeable); flipping the live video flags changes
> `ModeLabel`; the pusher emits exactly one `ST` frame per real change carrying the true value; equal
> snapshots → equal bytes. Full suite **7232 passed / 0 failed / 6 skipped** (+7 new), warning-clean. **UAT**
> (no browser MCP; HTTP+WS frame level, demo branch — no Apple ROM cached): `GET /`+`/app.js` 200, the served
> `app.js` carries the structured-`ST` decoder, the legacy `ST demo` text frame leads (backward-compat
> preserved), 145 binary `FB` frames stream at 196616 bytes (256×192), zero server errors. **P ships the
> SEAM + wire + minimal read-only render** — the control-strip DOM is row **T** (consumes this + exposes
> `window.machineStatus`). **Next: Q** (the Disk II runtime image swap, deps F/G ✅ — planned). Planner —
> **surface-UI batch P + Q PLANNED** (the `ST` status seam + the runtime disk-swap mechanism, the first two surface-arc rows), grounded against `main` @ `c26faac` (PRs #99–#117). **P** ([plan](superpowers/plans/2026-06-20-apple2-pr-p-status-frame.md), no deps): the `ST` status frame becomes a **structured JSON text frame** (the wire tag `ST` is reused — the client already routes all text→`handleStatusText`; the binary FB/AU path untouched) carrying board name + asset state + the live video-mode label (derived from the real `Apple2VideoState` flags, via a new read-only `Apple2Video.ModeLabel`) + per-drive **real** motor (a new production `Apple2DiskII.MotorOn` — the `$C0E8/$C0E9` + 556 off-delay, **NOT faked on insert**) + image label; a session-level `StatusPusher` pushes on the pump tick **only when the snapshot changes**; `app.js` decodes it read-only + exposes `window.machineStatus` for row T. Gate (headless, asset-free): driving the **real** `$C0E9` motor switch through the live bus flips `Status().Drives[0].MotorOn` (false at boot — un-fakeable), flipping the live video flags changes `ModeLabel`, and the pusher emits exactly one `ST` frame per real change carrying the true value. **P ships the SEAM + wire + minimal read-only render** — the control-strip DOM (drive panels/eject/library/upload) is row **T**, which consumes this. **Q** ([plan](superpowers/plans/2026-06-20-apple2-pr-q-disk-runtime-swap.md), deps F,G ✅): `Apple2DiskII` gains **two drive slots** (the shipped single `_image` becomes `_drives[1..2]`; the read routes through the **selected** drive — drive 2 becomes real for the first time, `$C0EB`-select now changes what the head reads) + runtime `Insert(int,IFluxImage)` / `Eject(int)` + a surface `InsertDisk(drive,bytes,DiskFormat)` / `EjectDisk(drive)` over a new `DiskImageFactory.FromBytes` (the bytes→flux builder R/S both call). The single-image constructor is **unchanged** — every shipped surface/test builds drive 1 as before (the byte-for-byte regression gate). Gate (interpreter, synthetic images): a running machine `InsertDisk(1, dskBytes, Dsk)` then reads a real GCR nibble off the runtime-inserted image via the live `$C0E9`/`$C0EC` bus, and after `EjectDisk(1)` 50k polls latch **nothing** (an empty drive — un-fakeable). **Two drifts flagged:** (1) **no `WozFluxImage` in `src/`** — Q satisfies the design's "both `.woz` and `.dsk`/`.po`" at the **seam** (the controller inserts a `.woz`-shape `IFluxImage` identically, proven with `SyntheticFluxImage`) and wires `.dsk`/`.po` end-to-end, but throws an explicit `NotSupportedException` for `.woz` **bytes** until a thin `WozFluxImage` parser follow-on lands (separable; R's `.dsk`/`.po` library path is unblocked). (2) **D14 says "a small additive `ST` frame"** — the shipped `ST` was a one-shot UTF-8 asset string; P **reuses the tag as structured JSON text** rather than minting a binary opcode (deliberate, no design behavior lost; the Spectrum/demo legacy one-shot text frame is preserved so the asset-free smoke test stays green). **Both P + Q are Builder-ready** — P (no deps) is the topmost eligible row; Q follows (deps F,G ✅); take the lower id (P) first per the queue rule. **P and Q each add the `Apple2DiskII Disk` field to the surface records — whichever ships first adds it, the second reuses it (identical additive change).** R/S/T stay `JIT` (R + S depend on Q shipping; T depends on P/R/S). **Next: Builder picks up P (or Q).**). (Builder — **O SHIPPED (PR #117) — the CP/M-display capstone / "usable 80-column CP/M" deliverable is STRUCTURALLY COMPLETE**: composes K (SoftCard dual-CPU CP/M) + N (Videx) so the CP/M `A>` renders on the 80-col Videx, the host `DisplayMultiplexer` auto-switching Apple-40 → Videx-80 guest-driven (no UI toggle). **`SoftCardVidexBoard.Spec`** (one validator-clean `$C000-$CFFF` carve: `$C600` disk-boot ROM + `$C500` control port + Z80 `CoprocessorSpec` + `$C800` Videx slot + `$CC00` VRAM), **`SoftCardVidexSurface`** (the `SoftCardSurface` twin + the Videx behind a `DisplayMultiplexer([video,videx])` whose active source follows `videx.ActiveChanged` — the one new behavior; the Videx is factory-Realized, not double-Realized), **`VidexRom`** (optional firmware+char loader, synthetic fallback), **`get-videx-roms.{sh,ps1}`** (never vendored), `Program.cs` (SoftCard branch → `SoftCardVidexSurface`, assetState `softcard-cpm-videx`; Apple/Spectrum/demo byte-for-byte), `app.js` (the new banner mapping). Full suite **7225 passed / 0 failed / 6 skipped**, warning-clean. **Pre-merge review: NO findings** (all 6 high-risk surfaces clean — the board carve tiles `$C000-$CFFF` with no gap/overlap + every slot Mmio-contained [full arithmetic trace], the auto-switch index polarity correct + no double-Realize, the `Program.cs` SoftCard-branch-only change, the `VidexRom` null-on-absence/throw-on-corrupt contract, the boot gate un-fakeable on 4 dimensions, the fail-soft fetch scripts) — **no fixer needed.** **Interpreter-tier resolution of the queue's "both tiers"**: the Z80 coprocessor is interpreter-only (ADR 0015 D4) — the gate is a single `[SoftCardCpmFact]`, not a both-tiers `[Theory]`. **The headline CP/M-on-Videx boot gate is asset-gated, SKIPPED** here (no Apple ROM + CP/M `.dsk` cached — a skipped gate is GREEN): the **live 80-col CP/M render awaits owner assets** (run `get-apple2-roms` + `get-softcard-cpm`; Videx ROMs optional). UAT (no browser MCP; HTTP frame level): server boots clean, `GET /`+`/app.js` 200, the updated `app.js` (with the `softcard-cpm-videx` mapping) served, zero server errors — the SoftCard-branch change did not regress the demo fall-through; `WebServerSmokeTests` green. **STOP per protocol:** the remaining eligible rows — **P** (`ST` status frame, no deps) + **Q** (disk affordance, deps F/G ✅) — are `JIT`-unplanned (R/S/T blocked on Q; L deferred). The Planner plans the surface-UI sub-arc (P/Q…) next. Builder — **N SHIPPED (PR #116)**: the **Videx Videoterm** 80-col card — `VidexVideoterm : IPeripheral, IDisplayDevice` (the 6845 CRTC at `$C0B0`/`$C0B1`, 2 KiB VRAM as 4×512-byte banks into `$CC00–$CDFF`, the `$C800` firmware window, an 80×24 RGBA render through the synthetic `VidexFont`, the guest-driven `ActiveChanged` signal), the `$C800` mapper as the **2nd `Remap` consumer** (Realize Remaps firmware→ROM + VRAM→active-bank-RAM; `SelectBank` re-points `$CC00`), the IOU `$C0Bx` delegate (peek-free), and `Apple2Board.SpecWithVidex`. Full suite **7220 passed / 0 failed / 5 skipped**, warning-clean. **Pre-merge review found 1 HIGH (fixed):** the IOU `$C0Bx` delegate dropped the written value (the LC/Disk II `Access(offset,isRead)` signature copied verbatim carries no write byte — so a real `STA $C0B0`/`$C0B1` could never program the CRTC through the bus; the gate passed only because `Program80x24` wrote `videx.Write(...)` directly). Threaded `(byte)value` through `Write`→`ApplyAnyAccessSideEffect`→`Access(offset,isRead,writeValue)` (LC/Disk II + the peek-free short-circuit untouched) + added an un-fakeable end-to-end bus-path CRTC gate (programs 40×20 via `bus.Write8`, asserts 280×180 — a dropped value falls back to the 80×24 default, which the assertion rejects). **1 MEDIUM surfaced to owner, NOT fixed (plan-acknowledged PR-O deferral):** the `$C0B8-$C0BF` bank-select decode `(o>>2)&3` reaches only banks 2/3 via the hardware path (the gate uses `SelectBankForTest`) — refine the exact decode against the real CP/M driver / Videx firmware 2.4 when the asset is fetched (PR-O). The synthetic-glyph render gate ran + passed (CRTC→80×24, VRAM+synthetic-charROM→inked RGBA, the bank `Remap`, the `DisplayMultiplexer` switch); the IOU/board paths did not regress (bare board byte-for-byte; every `Apple2*`/`SoftCard*`/`Spectrum*` test green). **`IFastMemoryProvider` confirmed NOT in `src/`** — `VidexVideoterm : IPeripheral, IDisplayDevice` only; fast-RAM intent via `Remap`-to-RAM. UAT (no browser MCP; HTTP+WS frame level): server boots clean, `GET /`+`/app.js`+`/index.html` 200, zero server errors, `WebServerSmokeTests` green. **Next: O** (CP/M-on-Videx capstone, deps K,N ✅). Planner — **CP/M-display batch N + O PLANNED** (the "usable 80-column CP/M" deliverable, ADR 0016): the Videx 80-col card + the CP/M-on-Videx capstone now have detailed bite-sized plans, grounded against `main` @ `59c1c05` (PRs #99–#114). **N** ([plan](superpowers/plans/2026-06-20-apple2-pr-n-videx-videoterm.md), deps A,M ✅): `VidexVideoterm : IPeripheral, IDisplayDevice` — the 6845 CRTC at `$C0B0`/`$C0B1`, 2 KiB VRAM as 4×512-byte banks into `$CC00–$CDFF`, the `$C800` firmware window, an 80×24 RGBA render through a **synthetic** char ROM (`VidexFont`), the `$C800` mapper as the **2nd `AddressSpace.Remap` consumer** (after the LC), and `ActiveChanged` — the guest-driven active-display signal wired to the shipped `DisplayMultiplexer.SetActive` (PR-M). Gate (asset-free, always-runs): the CRTC init → 80×24, VRAM+synthetic-charROM → inked 80×24 RGBA, the bank `Remap`, and a `DisplayMultiplexer` over `[apple40, videx80]` switches on the signal. The IOU delegates `$C0Bx` (the `$C08x`→LC / `$C0Ex`→Disk II pattern); `Apple2Board.SpecWithVidex` carves the `$C800` band validator-clean. **O** ([plan](superpowers/plans/2026-06-20-apple2-pr-o-cpm-on-videx.md), deps K,N): `SoftCardVidexBoard` (SoftCard coprocessor + `$C500` control port + the Videx `$C800` window in one validator-clean band) + `SoftCardVidexSurface` (the `DisplayMultiplexer([apple40,videx80])` whose active source follows the Videx's `ActiveChanged` — the guest-driven auto-switch, no UI toggle) + `VidexRom` + `get-videx-roms.{sh,ps1}` (optional Videx ROMs; synthetic fallback). Gate: the real `$C600`→tracks→`$CnXX` boot hands off to the Z80, CP/M's terminal driver enables the Videx → the multiplexer switches → `A>` paints on the **Videx 80-col render** (`ActiveIndex==1` + structural ink + `CoprocessorActive`) — **interpreter-tier** (the row's "both tiers" is imprecise; the coprocessor has no JIT path — ADR 0015 D4), **asset-gated/skip-with-note**. **Two shipped-API drifts flagged in the plans + carried below:** (1) **`IFastMemoryProvider` is NOT in `src/`** (ADR-0009-designed, never shipped — like `TimingTier`); N drops it and realizes the fast-RAM intent through the shipped `Remap`-to-RAM seam (VRAM = plain writable RAM behind the `$C800` window). (2) **the queue row O's "both tiers" is imprecise** — the CP/M/Z80 path is interpreter-only. **Both N + O are Builder-ready** — N (deps A,M ✅) is the topmost eligible row; O follows N. **Next: Builder picks up N.**). (Builder — **K SHIPPED (PR #113)**: the dual-CPU **capstone** — the SoftCard board composes the shipped seams (`Apple2Board.SpecWithSystem` + `WithCoprocessor(Z80)`/`CoprocessorSpec` + `SoftCardTranslation` + `SoftCardControlPort` at `$C500`), the CP/M data-track skew (`SectorOrderKind.Cpm`, research §5) lands in the shipped `.dsk` adapter, plus `SoftCardCpm` (the 143,360-byte loader), `SoftCardSurface`, and `get-softcard-cpm.{sh,ps1}` (Asimov mirror, never vendored). Full suite **7203 passed / 0 failed / 5 skipped**, warning-clean. The headline CP/M-to-`A>` boot gate is **asset-gated, SKIPPED** (no `.dsk`/system ROM cached here) — a skipped gate is GREEN; the **live CP/M-to-`A>` confirmation is pending the fetched asset** (owner runs `get-softcard-cpm` + `get-apple2-roms`). Web-surface UAT (no browser MCP; HTTP+WS frame level): server boots, `GET /`+`/app.js` 200, WS 101, `ST` text frame leads, `FB` frames stream 256×192, zero server errors — the modified `Program.cs` did not regress the Apple/Spectrum/demo fall-through. Pre-merge review: 1 MEDIUM (placeholder fetch URL — by-design per the plan; mitigated with a placeholder-detection guard) + 1 LOW (lazy `.dsk` probe) both fixed. **M SHIPPED (PR #114)**: the ADR 0016 Decision 1 active-display seam — `DisplayMultiplexer : IDisplayDevice` (Core) delegates `Width`/`Height`/`RenderInto`/`FrameReady` to the active of N sources, `SetActive(int)` fires `FrameReady` on an actual switch (so the surface re-pulls + re-sizes), a dormant source's frames are dropped, single-source is transparent; + the one `MachineHost` change — drop `readonly` on `_rgba`, `EnsureFrameBuffer()` re-allocs to the active source's geometry only when it changed (a strict no-op for every shipped fixed-size display). Full suite **7211 passed / 0 failed / 5 skipped**, warning-clean. Pre-merge review: **no findings** (all 5 high-risk surfaces clean — closure-capture, the no-op `SetActive` guard, the single-source byte-for-byte invariant, the buffer-size consistency window, gate un-fakeability). UAT (live web stack, single-source path): 12 consecutive `FB` frames stable at 256×192 / 196616 bytes — the re-size never spuriously fired; the multi-source switch is covered by the deterministic `MachineHostResizeTests` gate. **STOP per protocol:** the next eligible rows — **N** (Videx, deps A,M ✅), **O** (deps K,N), **P** (ST status), **Q** (disk affordance, deps F/G) — are all `JIT`-unplanned. Planner plans N+O (the Videx/CP/M-display path) next, grounded against shipped M. Planner — **rows K + M PLANNED** (next batch). **K** ([plan](superpowers/plans/2026-06-20-apple2-pr-k-cpm-boot.md), the dual-CPU **capstone**, deps E/F/H/J ✅): composes the shipped seams into the SoftCard board — `Apple2Board.SpecWithSystem` + `WithCoprocessor(Z80)` via `CoprocessorSpec` + the shipped `SoftCardTranslation` + `SoftCardControlPort` → `SoftCardBoard.Spec`; adds the CP/M data-track skew (`SectorOrderKind.Cpm`, research §5) to the shipped `.dsk` adapter, the `SoftCardCpm` 143,360-byte `.dsk` loader, the `SoftCardSurface` (CP/M in drive 1), and `get-softcard-cpm.{sh,ps1}` (Asimov mirror, never vendored, sign-off GIVEN). Gate: the real 6502 `$C600`→tracks→`$CnXX` boot hands off to the Z80, CP/M runs **translated** to `A>` on the Apple 40-col render (interpreter tier; the Videx 80-col is PR-N/O) — **asset-gated, skip-with-note** when the `.dsk` (or system ROM) is absent (a skipped gate is GREEN). **M** ([plan](superpowers/plans/2026-06-20-apple2-pr-m-display-multiplexer.md), no deps): the ADR 0016 Decision 1 active-display seam — `DisplayMultiplexer : IDisplayDevice` (Core, delegates `Width`/`Height`/`RenderInto`/`FrameReady` to the active source, `SetActive` fires `FrameReady`) + the one `MachineHost` change (drop `readonly` on `_rgba`, re-size to the active source's geometry per frame). Gate: switching the active source makes the host re-pull at the new size; the single-source path is byte-for-byte unchanged. Plans grounded against `main` @ `10f5737` (PRs #99–#111). **Both are immediately Builder-eligible** — K (deps E/F/H/J ✅), M (no deps); take the lower id (K) first per the queue rule. **N (Videx) + O (CP/M-on-Videx) stay `JIT`** — planned next against shipped M (N needs M) / K + N (O). **Next: Builder picks up K (or M).**). (Builder — **dual-CPU arc batch 1 SHIPPED (rows I + J)**. **I** (dual-CPU `Machine`/`MachineBuilder` scaffolding, PR #110): the single-CPU path is **provably byte-for-byte unchanged** (every pre-existing test still passes). **J** (`SoftCardTranslation` 6-branch table + `SoftCardControlPort`, PR #111): a real Z80 runs translated against shared 6502 RAM, the 6-branch boundary regression kills the refuted `+$1000` shortcut at branches 2–5, full suite **7196 passed / 0 failed / 4 skipped** (+25 new PR-J tests, purely additive — no shipped source touched). **K** (CP/M boot, deps E/F/H/J — now all ✅) is the topmost 📋 but its Plan is `JIT`-unplanned → **Builder STOPS per protocol; Planner plans K next** (grounded against shipped I/J). Planner — **dual-CPU arc batch 1 (rows I + J) PLANNED**: ADR 0015's biggest abstraction is now bite-sized. **I** ([plan](superpowers/plans/2026-06-20-apple2-pr-i-dual-cpu-scaffolding.md)) extends the shipped single-CPU machine model to two CPUs over one shared program space — `CoprocessorSpec?` on `BoardSpec`, `WithCoprocessor`, `IAddressTranslation`/`TranslatingAddressSpace`/`ICoprocessorControl`, the run-one-then-the-other dual-CPU `Run` (6502-domain virtual clock, all-IRQ-to-primary, dormant core never scheduled), with the **single-CPU path byte-for-byte unchanged** as the load-bearing regression gate. **J** ([plan](superpowers/plans/2026-06-20-apple2-pr-j-softcard-translation.md)) adds the concrete `SoftCardTranslation` (the 6-branch MAME-verified table — the refuted `+$1000` shortcut fails branches 2–5; 1 & 6 coincide), `SoftCardControlPort` ($CnXX-write active-CPU toggle, peek-free), with a real Z80 routine running translated against shared 6502 RAM as the end-to-end gate. Plans grounded against `main` @ `d685b0c`. **I is immediately Builder-eligible** (dep A ✅); **J follows I**. K (CP/M boot) stays `JIT` — planned against shipped I/J next. **Next: Builder picks up I.**). **Owner:** Mark.
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
| **H** | `Apple2Surface` + `get-apple2-roms.{sh,ps1}` + ROM-boot gate | ✅ | C, D, E, F, G | [plan](superpowers/plans/2026-06-20-apple2-pr-h-surface-and-rom-boot.md) | With the system + char-gen ROMs fetched, the ][+ boots to a **live BASIC prompt** (`>` Integer or `]` Applesoft — ROM-agnostic structural assertion: mostly-blank text screen + an ink floor either BASIC clears; rebuilt on the no-boot-ROM `SpecWithDiskII` board the live surface uses) on **both** tiers; DOS 3.3 boots from a `.dsk` in drive 1. **Asset-gated** (skip-with-note absent). **Live-verified 2026-06-21** with an owner-supplied Integer-BASIC Autostart ROM. |
| **I** | Dual-CPU `Machine` / `MachineBuilder` scaffolding (`CoprocessorSpec`) | ✅ | A | [plan](superpowers/plans/2026-06-20-apple2-pr-i-dual-cpu-scaffolding.md) | `CoprocessorSpec` + `WithCoprocessor` + the dual-CPU `Run` build a 2-CPU machine; the **single-CPU path is byte-for-byte unchanged** (every existing board regression-identical); all interrupts route to the primary 6502; the dormant core is never scheduled. |
| **J** | `SoftCardTranslation` (6-branch table) + `TranslatingAddressSpace` + `SoftCardControlPort` | ✅ | I | [plan](superpowers/plans/2026-06-20-apple2-pr-j-softcard-translation.md) | All **6** translation branches assert at their boundaries (`$AFFF→$BFFF`, `$B000→$D000`, `$EFFF→$CFFF`, `$F000→$0000`, …) — the refuted `+$1000 mod 64K` shortcut fails branches **2–5** (branches 1 & 6 coincide); the control-port write flips `_z80Active` and ends the slice. |
| **K** | Interpreter-tier CP/M boot wiring (`$C600`→tracks→`$CnXX`-start) | ✅ | E, F, H, J | [plan](superpowers/plans/2026-06-20-apple2-pr-k-cpm-boot.md) | The real SoftCard boot sequence (6502 `$C600` reads tracks `$00–$02`, sets LC banking, writes `$CN00`) hands off to the Z80; CP/M reaches its load state on the **interpreter** tier. **Asset-gated** on the SoftCard CP/M `.dsk` (skip-with-note absent). |
| **L** | JIT-under-translation (pre-translated physical fastmem) | ⏸️ | K | JIT | *(deferred/optional, ADR 0015 Decision 4 — measure interpreter CP/M throughput first.)* The Z80-under-translation gets fastmem over the physical backing arrays; parity-gated against the running interpreter SoftCard (the oracle). |
| **M** | `DisplayMultiplexer` + `MachineHost` per-frame re-size | ✅ | — | [plan](superpowers/plans/2026-06-20-apple2-pr-m-display-multiplexer.md) | The multiplexer delegates `Width`/`Height`/`RenderInto`/`FrameReady` to the active source; `SetActive` fires `FrameReady`; `MachineHost` re-sizes its `_rgba` buffer when dimensions change; a single-display board is transparent (no behavior change). |
| **N** | `VidexVideoterm` (`IPeripheral`+`IDisplayDevice`) + `$C800` expansion-bank mapper | ✅ (PR #116) | A, M | [plan](superpowers/plans/2026-06-20-apple2-pr-n-videx-videoterm.md) | The 6845 CRTC programmed via `$C0B0`/`$C0B1` + the init table (R1=`$50`/R6=`$18`) yields 80×24; VRAM+synthetic-charROM → RGBA; the `$C800` mapper (2nd `Remap` consumer) banks the firmware window + the `$CC00–$CDFF` VRAM bank; the Videx's `ActiveChanged` makes a `DisplayMultiplexer` `SetActive`. Synthetic char ROM, no asset. *(`IFastMemoryProvider` dropped — ADR-0009-designed, never shipped; the fast-RAM intent via `Remap`-to-RAM. See plan drift note.)* |
| **O** | Videx + CP/M asset scripts (`get-videx-roms`, `get-softcard-cpm`) + CP/M-on-Videx end-to-end gate | ✅ (PR #117) | K, N | [plan](superpowers/plans/2026-06-20-apple2-pr-o-cpm-on-videx.md) | With all assets fetched, booting the CP/M disk widens the display to the **80-col Videx terminal** (the `DisplayMultiplexer` auto-switches Apple-40 → Videx-80, guest-driven) and reaches the `A>` prompt — **interpreter-tier** (the row's "both tiers" is imprecise; the CP/M/Z80 side is interpreter-only per PR-K/ADR 0015 D4). **Asset-gated + owner-sign-off-given** (skip-with-note absent). |
| **P** | The `ST` status-frame seam (host→client read-only indicators) | ✅ (PR #119) | — | [plan](superpowers/plans/2026-06-20-apple2-pr-p-status-frame.md) | A new lightweight `ST` wire frame carries board name, asset state, per-drive motor + image label, video-mode label; the client renders them read-only; the host pushes real machine state (not faked). *(Designer T-A — suggested early; most surface indicators consume it.)* |
| **Q** | In-session disk insert / eject mechanism (Disk II runtime image swap) | ✅ (PR #120) | F, G | [plan](superpowers/plans/2026-06-20-apple2-pr-q-disk-runtime-swap.md) | The Disk II controller accepts "load these bytes as drive N's image" + "eject drive N" at runtime, for both `.woz` and `.dsk`/`.po`, via the `IFluxImage` seam; a running machine swaps images without rebuild. *(Designer T-D — shared dep of the two disk-UX paths.)* |
| **R** | `GET /disks` catalog endpoint + per-drive library dropdown | ✅ (PR #122) | Q | [plan](superpowers/plans/2026-06-20-apple2-pr-r-disk-library.md) | The server lists the cached `disks/` images (name, format, drive-compat, CP/M grouping); both per-drive `[ Library ▾]` selects populate from it; an empty catalog disables the select with the named-script hint. **Folds in the drive-2 status deferral (PR-Q): the `ST` frame now reports BOTH drives.** Gate: the endpoint lists a seeded cache dir + selecting an entry inserts it into drive N (reuse Q's runtime insert). *(Designer T-C.)* |
| **S** | Disk-upload inbound-binary path (the NEW binary WS frame + validation + UPLOADING state) | ✅ (PR #123) | Q | [plan](superpowers/plans/2026-06-20-apple2-pr-s-disk-upload.md) | Client `<input type=file>` → client validation (ext / 2 MB cap / non-empty) → binary WS `DK` frame → **server** re-validation (`.dsk`/`.po` exact length / `.woz` magic) → load into drive N; the UPLOADING → INSERTED / error states drive the panel. Gate: a binary `DK` frame with a valid `.dsk` inserts into drive N (server rejects a bad length/magic); the single-text-frame protocol is unaffected. *(Designer T-B — the surface's first inbound binary path; explicitly its own task. **Best taken after R** — reuses PR-R's `insertDisk` hoist + four-arg `InsertDisk` + the drive-2 fold-in; the plan notes the port-forward if S lands first.)* |
| **T** | Control-strip UI (drive panels, lights, mode label, asset banner) + keyboard T-F incl. D5 ctrl-wiring | ✅ (PR #125) | P, R, S | [plan](superpowers/plans/2026-06-20-apple2-pr-t-control-strip.md) | Two bordered drive panels (library select + upload + eject + a real-motor amber light driven by `$C0E8/$C0E9` + the 1 s off-delay, **not** faked on insert); the calm named-script asset banner replaces the silent fallback; the read-only mode label; one new `--drive-active` token. Binds to PR-R's `window.diskCatalog`/`insertFromLibrary`/`ejectDrive` + PR-S's `window.uploadDisk`/`uploadState`. **Plus D5 (T-F scope = B/full per Coordinator):** `KeyEvent.Ctrl` + `TryDecodeKey` reads `ctrl` + `Apple2KeyMap`/`Apple2Keyboard` fold a letter with `$1F` + `app.js` sends `ctrl` + `preventDefault`s `Ctrl+B`/`Ctrl+C`. **Own un-fakeable interpreter gate: a `Ctrl+B` event latches `$02` (not `$42`) at `$C000`.** Hybrid gate: (a) C# served-asset content assertions (`WebApplicationFactory`); (b) the shipped wire/seam gates; (c) the D5 interpreter gate. In-browser visual confirmation = owner UAT. *(Designer T-E/T-G/T-H + keyboard extensions T-F/D5.)* |
| **W** | `WozFluxImage` — a thin `.woz`-file byte parser → `IFluxImage` | 📋 | F | JIT | The missing half of "full `.woz` fidelity upfront" (the locked decision): PR-F shipped the `.woz`/LSS **read path** + the `IFluxImage` track-bitstream **seam**, but no `.woz`-**file** byte parser. `WozFluxImage` parses the WOZ1/WOZ2 container (INFO/TMAP/TRKS chunks → per-track bitstreams) into an `IFluxImage` the controller reads identically to the shipped `SyntheticFluxImage`/`DskFluxImage`. Unblocks raw `.woz` in `DiskImageFactory.FromBytes` (today an explicit `NotSupportedException`), in the R library list (today listed-disabled), and in S upload (today validates magic then the honest not-yet-supported reject). *(Separable IFluxImage follow-on — backlog; plan JIT when it reaches the front.)* |
| **SU** | ZX Spectrum 48K ROM UAT — multi-variant boot (variant × tier) + interactive BASIC | 📋 | — | [plan](superpowers/plans/2026-06-21-spectrum-48k-rom-uat.md) | **Recalibrates** the shipped Spectrum boot gate (`BootCycles` 200k→**7M** — full boot to the (C) screen is ≈5.9M T-states, stable by ~13M; the 200k was ~30× too small + the "≈140k/two frames" comment is wrong) and **parameterizes** it across the owner's six 48K ROM variants (canonical `spec48` + arabic-v1/v2/v31 + beckman + prototype, each 16384 B) **× both tiers**: every present variant boots to its copyright screen on Interpreter AND Jit — structural assertion (mostly-white `Colors[7]`=`0xFFD7D7D7` paper + black `Colors[0]`=`0xFF000000` ink, variant-safe floor `>50`, canonical `>200`) + a per-variant committed RGBA hash (captured on first green; both tiers identical). Adds a `<cache>/spectrum/variants/<name>.rom` discovery helper (`SpectrumRomVariants.Discover`, skip-with-note when none) + `get-spectrum-rom-variants.{sh,ps1}` (owner-copy, never vendored). **Plus an interactive BASIC UAT** (canonical ROM, both tiers): boot to the `K` cursor, drive the key matrix to type `PRINT 2+2`+ENTER (keyword `P`→`PRINT`, `SymbolShift`+`K`=`+`, `Enter`) and assert the printed `4` appears in the top print rows (ink-delta + committed hash) — boot → keyboard → BASIC interpreter → screen end-to-end. **Asset-gated** (skip-with-note when no Spectrum ROM cached). 48K-only (128/+2/+3 are a separate future arc). Flags (not built): a `--board` surface override + the Tester scratch cleanup (`tools/SpectrumProbe/`, `tools/WsProbe/`, `.uat-artifacts/`). |
| **CPM-1** | Honest main: per-track CP/M skew (the verified fix) + de-fanged boot gate (no false pass) | 🚀 | — | [plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr1.md) | **Restores GREEN main** (verified: `main` @ `1d0232c` is **2-failed** on a machine with the CP/M assets cached — the two `[SoftCardCpmFact]` `A>` gates fail at `CoprocessorActive`/`ActiveIndex`; this blocks PR #128's clean merge). Lands **ADR 0017 Decision 1** — per-track CP/M skew: boot interleave `[0,11,6,1,12,7,2,13,8,3,14,9,4,15,10,5]` (`(p×11)%16`) for system tracks 0–2, the existing data table `[0,6,12,3,9,15,14,5,11,2,8,7,13,4,10,1]` for tracks 3+, via a new `Apple2SectorOrder.PhysicalToLogical(kind,track)` overload (DOS/ProDOS ignore `track` — single-skew unaffected, regression-guarded) + `DskFluxImage` resolving the skew per track. **De-fangs both CP/M boot gates** (Decision 5): replaces `onPixels>50` + `PLACEHOLDER` hash with an honest **negative** assertion (boot2 no longer BRKs to the monitor) + a `[Fact(Skip=…)]` named-skip for the `A>` part until CPM-4 (CPM-5 for the Videx gate) — the suite is GREEN, the gate can't lie. **Un-fakeable gate** (asset-free): per-track skew regression test (boot table for track 0, data table for track 3); the negative gate FAILS pre-fix (monitor `*`), PASSES post-fix. PR-1 alone does **not** reach `A>`. |
| **CPM-2** | `SoftCardControlPort.Read()` open-bus (toggle on write only) | 📋 | CPM-1 | [plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr2.md) | **ADR 0017 Decision 2** (amends ADR 0015 D3): `Read()` returns open-bus `0x00` with **no** `Toggle()`; only `Write()` toggles the active CPU. The read-toggle livelocked the SoftCard-detect poll → `CAN'T FIND Z80 SOFTCARD`. **Un-fakeable gate:** a read (even 1000 reads) → 0 toggles; a write → 1 toggle (the `ControlSpy` `Calls`); with CPM-1+CPM-2 the live CP/M screen no longer contains `CAN'T FIND` (decoded-text negative) and the Z80 activates during the detect (slice-and-OR `CoprocessorActive`). One-line production change. Still does not reach `A>` (CPM-3 needed for a stable handshake). |
| **CPM-3** | `RunDualCpu` yields at the `$CnXX` toggling instruction (Step-based) | 📋 | CPM-2 | [plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr3.md) | **ADR 0017 Decision 3** (amends ADR 0015 D1): the active core is driven one instruction at a time via `ICpuCore.Step()`, breaking the instant a `$CnXX` write sets `_sliceEndRequested` — so the switch lands **at the writing instruction**, not after the whole slice budget. Confined to the `_coprocessor is not null` branch; **single-CPU `RunSingleCpu` byte-for-byte unchanged** (full pre-existing suite green = the load-bearing regression gate). **Un-fakeable gate:** a synthetic dual-CPU yield test — CPU-A writes the control port then a sentinel store; the sentinel must NOT execute before the Z80 runs (FAILS pre-fix, PASSES post-fix); the live boot reaches the Z80 BIOS at `$Axxx` **stably** (no late fallback to the `$0000` reset stub). Still may not paint `A>` (CPM-4 bring-up). |
| **CPM-4** | The live `A>` deliverable: decoded-text gate (Decision 5) + `$1010` bridge bring-up (Decision 4) | 📋 | CPM-3 | [plan](superpowers/plans/2026-06-21-cpm-boot-to-a-prompt-pr4.md) | **THE HEADLINE.** With CPM-1–3 landed, brings the boot to `A>`; closes any residual `$1010` 6502-BIOS↔Z80 bridge item via **live triage against the real disk** (Decision 4 — Builder bring-up, may be a no-op if 1–3 are the complete gating set; not pre-designed). **Un-fakeable gate (Decision 5):** decode the 40-col Apple text page (`TextRowBase` walk, high-bit-stripped) and assert the **decoded `A>` substring** (the CCP prompt) + a CP/M sign-on line (`CP/M`/`DIGITAL RESEARCH`) + `CoprocessorActive==true`; **capture the real frame hash** (replace `PLACEHOLDER` in the SAME PR — the text substring is the primary oracle, the hash a tightening gate). **Asset-gated** (interpreter tier; the coprocessor has no JIT). Owner UAT = the visible `A>` in the browser surface. |

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
  the emulation-core arc and can interleave once their deps land. **R lands the drive-2 status fold-in**
  (the PR-Q deferral — the `ST` frame grows from 1 to 2 `DriveStatus` entries via a mutable per-drive
  label holder + a four-arg `InsertDisk(…,label)`); **S is best taken after R** (it reuses R's
  `insertDisk` hoist + four-arg insert + the drive-2 fold-in; its plan notes the port-forward if S lands
  first), though both are formally deps-Q-only.
- **The `.woz`-file parser (W)** is the missing half of the locked "full `.woz` fidelity upfront" decision:
  PR-F shipped the `.woz`/LSS **read path** + the `IFluxImage` **seam**, but no `.woz`-**file** byte
  parser. `WozFluxImage` (deps F ✅) is a separable follow-on — it unblocks raw `.woz` in
  `DiskImageFactory.FromBytes` (today `NotSupportedException`), in R's library list (today
  `supported:false`), and in S's upload (today validates magic then the honest not-yet-supported reject).
  It does **not** block R/S/T (the `.dsk`/`.po` paths are end-to-end-complete without it). `JIT`-unplanned
  in the backlog; plan it when it reaches the front.

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

- **PR-S — disk-upload inbound-binary path (the surface's first inbound binary WS message)** (2026-06-21,
  PR #123). The fourth surface-UI sub-arc row (design T-B / D12). A client `<input type=file>` (DOM is row T)
  → client validation (ext allow-list `.woz/.dsk/.po` / 2 MB cap / non-empty) → a binary **`DK`** frame
  (`'D','K',version(0x01),drive(1|2),formatByte(0=woz,1=dsk,2=po),...imageBytes`, `FrameCodec.TryDecodeUpload`
  → `UploadFrame`) → **server re-validation** (`UploadValidator`: `.dsk`/`.po` exactly
  `DiskImageFactory.DskBytes`=143360; `.woz` validates the `WOZ1`/`WOZ2` magic then returns the **honest**
  `.woz upload isn't supported yet — use .dsk or .po` reject — never reaches `insertDisk`, which throws
  `NotSupportedException` for `.woz`; empty body → "That file is empty") → load into drive N via the shipped
  R/Q `insertDisk` delegate → an upload-result ack (`FrameCodec.EncodeUploadAck` — an `ST`-prefixed
  `{"upload":{drive,ok,message}}` text frame the client routes to resolve UPLOADING → INSERTED / error).
  **The load-bearing detail — multi-fragment reassembly:** a `.dsk` is 143,360 bytes, far over the receive
  buffer (grown 1 KiB → 8 KiB), so a `DK` message arrives across many fragments; the receive loop accumulates
  into a `MemoryStream` until `EndOfMessage`, caps at 2 MiB, then decodes + validates + dispatches. The
  client gains `window.uploadDisk(drive,file)` (validate → `FileReader` → `Uint8Array` binary send) +
  `window.uploadState`/`uploadLastError` + the `st.upload` ack route (decoded before the status-snapshot
  path). **The single-text-frame protocol is unaffected** (the `DK` binary frame is additive; the text key +
  PR-R disk-insert/eject paths are byte-for-byte, including R's HIGH-fix try/catch, preserved through the
  receive-loop rewrite). **Pre-merge review: 0 HIGH, 2 MEDIUM + 1 LOW, all fixed.** M1: an oversized
  multi-fragment message reset the accumulator when the 2 MiB cap fired on a non-final fragment, then
  re-accumulated the tail and dispatched a partial frame (a misleading "corrupt" ack); added a `capExceeded`
  drain flag that ignores the rest of an over-cap message and acks "File too large" once at `EndOfMessage`
  (defense-in-depth — the client enforces the cap; only reachable by a bypassed client). M2: a valid `.dsk`
  uploaded to a session with no Apple disk drive (Spectrum/demo, `insertDisk == null`) acked "That image
  looks corrupt" — factually wrong; now "Disk upload isn't supported in this session" (the Apple branches
  always wire `insertDisk`, so this is only reachable from a non-Apple session / crafted request). L1: the
  client extension parse reached the format map by accident on a no-dot filename (`slice(-1)`); guarded the
  `-1` case explicitly. Full suite **7267 passed / 0 failed / 6 skipped** (+16 net new), warning-clean,
  stable across consecutive runs. The S gate tests use the `DispatchUpload` seam + board-agnostic WS health
  (mirroring R's structure) — they do **not** mutate the process-global `CPUEMULATOR_TESTVECTORS` (the
  isolation defect R surfaced + fixed). **UAT** (no browser MCP; live out-of-process server, ROM-absent →
  demo board): a real `ClientWebSocket` sent a genuine **143,365-byte `DK` frame** (which fragments over the
  wire) → the server reassembled + validated it + acked (`ok:false, "Disk upload isn't supported in this
  session"` — the demo board has no Apple drive, exercising the M2 path live + proving multi-fragment
  reassembly end-to-end); a 100-byte `DK` frame → `ok:false, "That image looks corrupt"`; a key event after
  the binary burst → 3 binary `FB` frames stream (text path healthy); zero server-side errors/exceptions
  logged. **The visible upload picker + UPLOADING/INSERTED/error panel is row T** (S ships the transport +
  validation + state machine T binds to). **STOP per protocol — R + S cleared.** **T** (control-strip UI,
  deps P/R/S ✅) is the next eligible row but is **`JIT`-unplanned**; **W** is a backlog `JIT` row; **L**
  deferred → Builder stops; **the Planner plans T (the final surface-UI row) next.**

- **PR-R — `GET /disks` disk-library catalog + per-drive library dropdown transport** (2026-06-21, PR #122).
  The third surface-UI sub-arc row (design T-C / D11/D13). A new **`DiskCatalog`** (`CpuEmulator.Machines`,
  beside `Apple2Rom`/`SoftCardCpm`) enumerates `<cache>/disks/*.dsk|*.po|*.woz` + the already-cached SoftCard
  CP/M `.dsk` into deterministic `DiskCatalogEntry`s (sorted, CP/M grouped last + flagged; `.woz` listed
  `supported:false` — no `WozFluxImage` parser yet, backlog row W); `TryResolve` maps a catalog id back to a
  path with a path-traversal guard (`fileName != Path.GetFileName(fileName)`). **`GET /disks`** serves the
  compact JSON the per-drive `[ Library ▾]` select fetches. The **`disk-insert`/`disk-eject` text-WS path**
  (`FrameCodec.TryDecodeDisk` → `DiskCommand`, drive 1–2 range-guarded, key JSON rejected) is dispatched in
  `ReceiveKeysAsync` **before** the key path (a disk JSON would otherwise decode as a `KeyCode.None` no-op
  key); a library insert resolves the id server-side, reads the cached bytes, and calls the shipped Q
  `surface.InsertDisk(drive,bytes,format,label)` — `.woz` is guarded out (it throws `NotSupportedException`).
  **Drive-2 status fold-in** (the PR-Q deferral): a tiny mutable `DriveLabels` holder per surface grows
  `Status()` from one to **two** `DriveStatus` entries (both report the shared one-motor `Disk.MotorOn` —
  correct for the real Disk II; only labels are per-drive); adds the four-arg `InsertDisk(…,label)`, keeps the
  two-arg Q overload; the one shipped single-drive assertion in `Apple2SurfaceStatusTests` updated to expect
  two. Client (`app.js`) gains read-only `loadCatalog()`/`window.diskCatalog` + `window.insertFromLibrary`/
  `window.ejectDrive` text senders (no panel DOM — row T binds to these). **Pre-merge review: 1 HIGH +
  1 MEDIUM, both fixed.** HIGH: the insert branch's `File.ReadAllBytes`+`DiskImageFactory.FromBytes` were
  unguarded — a vanished/truncated library file (TOCTOU after `TryResolve`, or a non-256-multiple length)
  would throw out of `ReceiveKeysAsync`, end the `recv` Task, and tear down the live WS session; now wrapped
  in a try/catch over the expected I/O + image-construction exceptions (a bad disk is a clean no-op). MEDIUM:
  `DriveLabels` fields (written on the receive thread, read on the pump thread via `Status()`) made
  `volatile`. **Deferred (justified):** M1 (multi-fragment WS text reassembly) is pre-existing on the key path
  and lands in **PR-S**'s receive-loop rewrite (8 KiB buffer + `EndOfMessage` accumulation for the
  143,360-byte upload); L1 (`TryDecodeKey` returns true for non-key JSON) is correct given the documented
  disk-before-key ordering. **Test-isolation fix (Builder):** the plan's literal `DiskLibraryEndpointTests`
  set the process-global `CPUEMULATOR_TESTVECTORS` to point the in-memory host at a seeded cache; under the
  assembly's parallel test collections the TomHarte/Klaus vector suites read that var live, so a concurrent
  vector theory resolved to the empty seeded dir and failed (flaky, count varied run-to-run). Rewrote the gate
  onto the `DiskCatalog.List(root)`/`TryResolve(root)` seam the plan itself documents ("so a test never
  mutates the process-wide env var") — the listing + un-fakeable nibble read-back drive the seam; the WS leg
  asserts the receive loop stays healthy on a `disk-insert` (board-agnostic). No production change for the
  isolation fix. Full suite **7251 passed / 0 failed / 6 skipped** (+12 net new), warning-clean, **stable
  across two consecutive full-suite runs** (the contamination is gone). **UAT** (no browser MCP; live
  out-of-process server, ROM-absent → demo board): `GET /` 200 (1397 B), `GET /app.js` 200 (8239 B, carries
  `loadCatalog`/`window.diskCatalog`/`insertFromLibrary`/`ejectDrive`), `GET /disks` 200 → `[]` (the
  empty-catalog path the client tolerates); a real `ClientWebSocket` session leads with the `ST demo` text
  frame, then a `disk-insert` (non-existent id — exercising the H1 resolve/TOCTOU path) + `disk-eject` +
  key-A burst, after which 5 consecutive binary `FB` frames stream at 256×192/196616 B — session healthy, the
  `/ws` request returned 101, zero server-side errors/warnings/exceptions logged. **The visible `[ Library ▾]`
  panel DOM is row T** (R ships the data + senders T binds to). **Next: S** (the upload binary path).

- **PR-Q — in-session Disk II insert/eject (the runtime image swap)** (2026-06-21, PR #120). The second
  surface-UI sub-arc row (design T-D / D11–D13): the `Apple2DiskII` controller accepts, **at runtime**, "load
  these bytes as drive N's image" + "eject drive N" — the **shared dependency** of R (library dropdown) and S
  (upload), both of which land bytes here. **Two drive slots:** the controller's single `_image` becomes a
  1-based `IFluxImage?[3] _drives` ([1]=drive 1, [2]=drive 2, [0] unused); `ReadDataLatch` routes through the
  **selected** `_drives[_drive]`, so `$C0EB`-select / **drive 2 becomes real for the first time** (PR-F/G
  tracked `_drive` via `$C0EA/$C0EB` but ignored it on read — single `_image`). The single-image constructor
  signature `Apple2DiskII(IFluxImage)` is **unchanged** (sets `_drives[1]`); `_image` is **fully removed**.
  **Runtime `Insert(int,IFluxImage)` / `Eject(int)` / `HasImage(int)`:** re-seek the active drive's head
  (`_bitPos = 0`) on a swap of the active drive, range-guard drives 1–2 (`ArgumentOutOfRangeException` on
  0/3), allow re-insert; an **empty drive reads nothing** (the `image is null` guard returns the not-ready
  latch, bit 7 clear — the un-fakeable eject proof). The stepper clamp now reads the selected drive's
  `TrackCount` (`_drives[_drive]?.TrackCount ?? 1`, so an empty selected drive clamps the head to track 0
  safely). The motor light still follows the real `$C0E9/$C0E8` switch + the ~1 s 556 delay — **never faked
  on insert** (D10). **`DiskImageFactory.FromBytes(bytes, format)`** (new) + **`DiskFormat{Woz,Dsk,Po}`**
  (new): the one place "these bytes → an `IFluxImage`" lives, shared by R + S — `.dsk` → `SectorOrderKind.Dos33`,
  `.po` → `ProDos` (both via `DskFluxImage` over a 256-byte-sector `DiskImage`); raw **`.woz` bytes throw an
  explicit `NotSupportedException`** (`DskBytes` const = 143360). **Surface `InsertDisk(drive,bytes,format)` /
  `EjectDisk(drive)`** on `Apple2Surface` / `SoftCardSurface` / `SoftCardVidexSurface` (the `Disk` record field
  shipped in PR-P — Q reuses it, the P/Q coordination field) — the **R/S call point**; Q adds **no** WebSocket
  message handler (R = the library text message, S = the binary `DK` upload frame — both blocked on Q
  precisely for this method). **Pre-merge review (focused on the single-image regression identity, the
  selected-drive + empty-drive read, the `_bitPos` wrap deviation, the stepper clamp `?? 1` fallback, the
  range guards + re-seek semantics, the factory + `.woz` exception, and the surface helper additive-ness)
  found NO HIGH bugs.** The one implementer deviation — `if (_bitPos >= bitLen) _bitPos %= bitLen;` in
  `ReadDataLatch` — was confirmed **correct, necessary, and correctly placed**: a `$C0EB` drive-select does
  NOT reset `_bitPos` (only a half-track step or an active-drive insert/eject does), so switching from a long
  track to a shorter selected track leaves the head past the new bitstream — a real `IndexOutOfRangeException`
  the plan's literal omitted; the wrap is a true no-op on the single-drive path (the read loop already keeps
  `_bitPos` in `[0,bitLen)`) and sits AFTER the `bitLen <= 0` guard so the `%` is safe. **No fixer needed.**
  **One MEDIUM surfaced + deferred (recorded for the Planner / R/S/T):** `Status()` (the PR-P `ST` frame)
  still reports a **single** drive entry though drive 2 is now real — a drive-2 indicator needs a `Drive2Label`
  threaded through `InsertDisk(2,…)` / `EjectDisk(2,…)`, genuine **row R/S/T** territory (the two-drive panel +
  per-drive label-tracking-on-insert live there, and consume this frame); the `ST` codec already carries an
  N-element `Drives` array, so R/S/T grow the 2nd `DriveStatus` with no Q change — deferred deliberately to
  keep Q scoped to "the mechanism + the call point" per the plan. **The un-fakeable gates** (interpreter tier,
  synthetic images, no asset, headless — the oracle): `Apple2SurfaceDiskSwapTests` — a real machine,
  `InsertDisk(1,dskBytes,Dsk)` at runtime, then driving the **live program bus** (`$C0E9` motor + `$C0EC`
  polls) reads a GCR nibble off the runtime-inserted `.dsk`, and after `EjectDisk(1)` 50k `$C0EC` polls latch
  **nothing** (no bit-7-set byte — an empty drive, impossible to fake); `DskFluxImageTests.A_runtime_inserted_
  dsk_image_is_read_back_through_the_head` — a runtime `.dsk` decodes back to a **known 256-byte track-0
  sector**; `Apple2DiskIITests` — the read follows the **selected** drive after a runtime insert into drive 2
  (distinct images, so a wrong-slot read fails), eject-then-reinsert restores reads, out-of-range drive
  throws. **The `.woz`-bytes drift (recorded):** the shipped tree has no `.woz`-file parser; Q satisfies the
  design's "both `.woz` and `.dsk`/`.po`" at the **seam** (`Insert` accepts a `.woz`-shape `IFluxImage`
  identically — the controller can't tell `SyntheticFluxImage` from a real `.woz` flux image, the OQ1-✅
  format-agnostic invariant) + wires `.dsk`/`.po` **end-to-end**, throwing an explicit, honest
  `NotSupportedException` for `.woz` **bytes** until a thin **`WozFluxImage`** parser follow-on lands
  (separable; R's `.dsk`/`.po` library path is unblocked, `.woz` library items wait on `WozFluxImage`).
  **UAT** (no browser MCP; HTTP+WS frame level — Q touches the shared disk-read hot path the IOU/Apple boards
  share): the live server boots clean, `GET /`+`/app.js` 200, the `ST demo` text frame leads (PR-P's status
  path intact), **115 binary `FB` frames stream at 196616 bytes** (256×192), **zero server errors** — the
  two-slot controller change did not regress the demo fall-through or the shared surface. Gate: full suite
  **7239 passed / 0 failed / 6 skipped** (the 7232 post-PR-P baseline + 7 new PR-Q tests, purely additive),
  warning-clean. **The surface-UI disk-swap mechanism is ready for R + S** (both deps Q ✅) — R lists +
  inserts `.dsk`/`.po` library items, S validates + uploads bytes; both call `surface.InsertDisk`. **Next: R
  + S become eligible but are `JIT`-unplanned → the Planner plans them.**
- **PR-P — the `ST` status-frame seam (host→client read-only indicators)** (2026-06-21, PR #119). The first
  surface-UI sub-arc row (design D14 / task T-A): the host pushes the **REAL** machine state to the browser
  client as a structured `ST` text frame whenever it changes. **`MachineStatus`/`DriveStatus` records +
  `FrameCodec.EncodeStatus`** — the wire tag `ST` is **reused** (now `"ST " +` compact, deterministic JSON)
  rather than minting a binary opcode, so the existing text⇒status / binary⇒pixels-audio client split is
  untouched (the FB/AU binary path unchanged); compact-JSON determinism (no `WriteIndented`, stable
  anonymous-type key order) ⇒ equal snapshots produce **byte-identical** frames (the change-detection
  contract). **`Apple2Video.ModeLabel`** (TEXT/HIRES/LORES/MIXED + page, derived from the SAME live
  `Apple2VideoState` flags the renderer reads — Mixed wins) + **`Apple2DiskII.MotorOn`** (the real `_motorOn`
  the `$C0E8/$C0E9` switches + the ~1 s 556 off-delay drive — **`false` at boot**, never set by inserting an
  image) — narrow **production** reads promoted from the former `*ForTest` inspectors (the inspectors stay).
  **Surface `Status()`** on `Apple2Surface` / `SoftCardSurface` / `SoftCardVidexSurface` (the Videx surface
  reads `Display.ActiveIndex == VidexIndex` for `"Videx 80×24 · CP/M"`); the **`Apple2DiskII Disk` +
  `Drive1Label` record fields are additive** (appended — the **P/Q coordination field: P added it, Q
  reuses it**). **`StatusPusher`** (session-level) pushes the `ST` frame on the `SurfacePump` tick **only when
  the snapshot changes** (byte-compare; the first tick always pushes the boot state); the Spectrum/demo
  **legacy one-shot `ST <assetState>` text frame is preserved** (those branches set `statusProvider = null`).
  **`app.js`** decodes the structured `ST` (JSON body) and falls back to the legacy bare-asset string,
  updating the status line **read-only** and exposing **`window.machineStatus`** for the control-strip UI
  (row T). **Pre-merge review (focused on the legacy-path backward-compat + `WebServerSmokeTests`, the real
  motor flag's un-fakeability, the `Program.cs` pump-tick wiring + teardown set + closure capture, the
  encoding determinism for change-detection, the private-`MotorOn()`→`TurnMotorOn()` rename completeness, the
  additive record shape, and the `ModeLabel` precedence) found NO HIGH findings** — all seven surfaces clean;
  2 MEDIUM were plan-intentional (the LORES+MIXED label collapses to `MIXED` per the spec's "Mixed wins"
  precedence; the `applyAssetBanner` call before the richer board+mode line is the plan's documented intent),
  2 LOW non-issues (the unused bare-`SoftCardSurface.Status()` path; the idle `sendStatus` task that tears
  down cleanly) — **no fixer needed.** One implementer adaptation: a private `void MotorOn()` helper collided
  with the new `public bool MotorOn` property → renamed `TurnMotorOn()` (the single `$C0E9` call site updated;
  review confirmed no missed call sites). **The un-fakeable gates** (headless, asset-free): driving the real
  `$C0E9` motor switch through the live bus flips `Status().Drives[0].MotorOn` to `true` (it is `false` at
  boot — a faked-on-insert indicator fails); flipping the live `Apple2VideoState` flags changes `ModeLabel`
  (a hard-coded label fails the HIRES/LORES/MIXED/page asserts); the pusher emits **exactly one** `ST` frame
  per real change carrying `"motor":true`; the codec round-trips every field + equal snapshots → equal bytes.
  **UAT** (no browser MCP; HTTP+WS frame level, demo branch — no Apple ROM cached): `GET /`+`/app.js` 200, the
  served `app.js` carries the structured-`ST` decoder (the `machineStatus`/`applyAssetBanner` glue), the
  legacy `ST demo` text frame leads (backward-compat preserved), **145 binary `FB` frames stream at 196616
  bytes** (256×192), **zero server errors** — the modified `Program.cs` did not regress the demo fall-through.
  Gate: full suite **7232 passed / 0 failed / 6 skipped** (the 7225 post-PR-O baseline + 7 new PR-P tests,
  purely additive), warning-clean. **P ships the SEAM + the wire + the minimal read-only render** — the
  control-strip DOM (drive panels / eject / library / upload + the amber motor light) is row **T**, which
  consumes this frame + `window.machineStatus`. The **live Apple-path browser UAT** (the drive light / mode
  label rendering) is asset-gated on the cached Apple ROMs + is row T's user-facing surface — a known gap,
  covered here at the wire level by the deterministic gates. **Next: Q** (the Disk II runtime image swap, deps
  F/G ✅ — planned; reuses the `Disk` surface field P added).
- **PR-O — CP/M-on-Videx end-to-end (the CP/M-display capstone / "usable 80-column CP/M" deliverable)**
  (2026-06-21, PR #117). The ADR 0016 capstone — composes the two shipped capstones **K** (the SoftCard
  dual-CPU CP/M board) and **N** (the Videx Videoterm) so the CP/M `A>` prompt renders on the **80-column
  Videx terminal**, with the host `DisplayMultiplexer` (PR-M) **auto-switching Apple-40 → Videx-80 when CP/M
  takes the Videx** — guest-driven, no UI toggle. **Pure composition + the Videx ROM fetch + the auto-switch
  wiring + the end-to-end gate; no K or N internals re-implemented.** **`SoftCardVidexBoard.Spec`** (new,
  `CpuEmulator.Machines`): one validator-clean `$C000-$CFFF` carve reconciling all four windows — the `$C600`
  disk-boot ROM (CP/M boots from disk) + the `$C500` SoftCard control port + the Z80 `CoprocessorSpec`
  (`SoftCardTranslation`, ratio 2.0) + the `$C800` Videx firmware slot + the `$CC00` VRAM RAM window (regions
  tile `$C000→$C600→$C700→$C800→$CC00→$CE00→$D000` with no gap/overlap, every slot Mmio-contained, the
  control-port name `"softcard"` matches the coprocessor's `ControlPortPeripheral`). **`SoftCardVidexSurface`**
  (new, `CpuEmulator.Surface.Web`): the `SoftCardSurface` twin + the Videx + a `DisplayMultiplexer([video,
  videx], initialActive: 0)` (Apple-40 active at boot); the **one new behavior** is the guest-driven auto-switch
  `videx.ActiveChanged += a => mux.SetActive(a ? VidexIndex : AppleIndex)` (ADR 0016 Decision 2 — CP/M's
  terminal driver enabling the Videx IS the switch); the host's display is the multiplexer so `MachineHost`
  re-sizes 280×192 ↔ 560×216 on the switch (PR-M); the Videx is Realized by the factory (its `"videx"` board
  slot) — NOT double-Realized in the surface (which would re-run the `$C800` Remap); the video + speaker are
  explicitly Realized (no board slot), `machine.Reset()` after. **`VidexRom`** (new): the optional Videx
  firmware (1 KiB) + char (2 KiB) ROM loader (the `Apple2Rom` twin) — returns null on absence (synthetic
  fallback: `VidexFont.Fallback` + an all-zero firmware), throws `InvalidDataException` only on a wrong-length
  file. **`get-videx-roms.{sh,ps1}`** (new, `tools/`): fetch-on-demand into `<cache>/videx/`, length-sanity-
  checked (1024/2048), **never vendored** (Asimov mirror; owner-confirmed placeholder URLs, the length check
  the real guarantee), both ROMs optional + fail-soft. **`Program.cs`**: the SoftCard branch boots
  `SoftCardVidexSurface.Create` (assetState `"softcard-cpm-videx"`); the Apple/Spectrum/demo branches
  byte-for-byte unchanged. **`app.js`**: maps `softcard-cpm-videx` → `"connected · Apple ][+ SoftCard · CP/M ·
  Videx 80-col"`. **Pre-merge review (focused on the board carve validator-cleanliness with a full
  region-tiling arithmetic trace, the multiplexer auto-switch index polarity + no-double-Realize, the
  `Program.cs` regression, the `VidexRom` null-on-absence contract, the boot-gate un-fakeability, and the fetch
  scripts) found NO findings at any severity** — all six high-risk surfaces clean; this is composition of two
  already-reviewed capstones with no new logic except the wiring. **No fixer needed.** **Interpreter-tier
  resolution of the queue row O's "both tiers"** (imprecise): the Z80 coprocessor is interpreter-only (ADR 0015
  Decision 4 — `BoardMachineFactory` builds it interpreter-tier regardless of board tier; CP/M's Videx output
  is produced by the Z80, so this path has no JIT tier) — the gate is a single `[SoftCardCpmFact]`, NOT a
  both-tiers `[Theory]`, exactly like PR-K. **The un-fakeable gates:** the board composition (Z80 coprocessor +
  control port + Videx window all wired, both `"softcard"` + `"videx"` slots present), the `VidexRom` loader
  (null-on-absence, throw-on-wrong-length), `SoftCardVidexSurface_constructs_renders_and_wires_the_auto_switch`
  (280×192 at boot, Z80 wired, `SetActiveForTest(true)` flips the multiplexer to the Videx index 1), and the
  headline **`Cpm_boots_and_renders_the_A_prompt_on_the_Videx_80col_interpreter`** — with the assets present,
  the real `$C600`→tracks→`$CnXX` boot hands off to the Z80, CP/M runs translated, the terminal driver enables
  the Videx → the multiplexer auto-switches → `A>` paints on the Videx 80-col render, asserted on FOUR
  independent dimensions (`Display.ActiveIndex == 1` [the auto-switch fired — a 40-col-only boot leaves it 0] +
  the Videx 560×216 geometry [not Apple 280×192] + structural `A>` ink on a mostly-blank Videx render +
  `machine.CoprocessorActive` [the Z80 ran the `$CnXX` handoff]) + a committed-hash placeholder (inert until
  captured). **Asset-gated, skip-with-note** when the Apple ROM or CP/M `.dsk` is absent (the PR-K/PR-H
  discipline — a skipped gate is GREEN); in this environment the gate **SKIPPED**, so the **live 80-col CP/M
  render is pending the fetched assets** (the owner runs `get-apple2-roms` + `get-softcard-cpm`; the Videx ROMs
  are optional). **UAT** (no browser MCP; HTTP frame level): the live server boots clean on `:5253`, `GET /`+
  `/app.js` 200, the updated `app.js` (6024 bytes, carrying the `softcard-cpm-videx` mapping) served over HTTP,
  **zero server errors** — the modified `Program.cs` SoftCard branch (inert with no assets) did not regress the
  Apple/Spectrum/demo fall-through; `WebServerSmokeTests` (the WS `ST`→`FB` frame stream) green in the suite.
  Gate: full suite **7225 passed / 0 failed / 6 skipped** (the 7220 post-PR-N baseline + 5 new PR-O tests; the
  CP/M-on-Videx boot gate joined the 5 prior asset/JIT-gated skips → 6), warning-clean. **The "usable
  80-column CP/M" deliverable is STRUCTURALLY COMPLETE** (pending owner assets for the live render). **The
  CP/M-display arc (M→N→O) is done.** The next eligible rows — **P** (the `ST` status frame, no deps) + **Q**
  (the runtime disk-swap mechanism, deps F/G ✅) — are `JIT`-unplanned (R/S/T depend on Q; L deferred). The
  Planner plans the surface-UI sub-arc next.
- **PR-N — `VidexVideoterm` 80-column card + the `$C800` expansion-bank mapper (the first DisplayMultiplexer
  consumer + the 2nd `Remap` consumer)** (2026-06-21, PR #116). The Videx Videoterm (ADR 0016 Decision 3,
  research §8) — the 80-col display the CP/M-on-Videx capstone (PR-O) renders to. **One new peripheral + one
  board-spec variant + the additive IOU delegate.** **`VidexVideoterm : IPeripheral, IDisplayDevice`** (new,
  `CpuEmulator.Peripherals`): the **6845 CRTC** programmed via `$C0B0` (register-select) / `$C0B1` (data) — the
  init table (R1=`$50`=80 cols, R6=`$18`=24 rows, R9=`$08`=9 lines/cell) yields **80×24 → 560×216** RGBA;
  **2 KiB VRAM as 4×512-byte bank arrays** banked into `$CC00-$CDFF`; an 80×24 monochrome render
  (`Apple2Palette.MonoOn`/`MonoOff`) walking the active VRAM bank through the char ROM (bit 6 = leftmost, the
  `Apple2Font` order); a never-zero default geometry guard (the multiplexer/host divide by `Width`/`Height`);
  and the guest-driven **`ActiveChanged(bool)`** active-display signal (ADR 0016 Decision 2 — the Videx is the
  WRITER, the host `DisplayMultiplexer` the READER; only fired on a real transition, the `SetActive` no-op-guard
  shape). **`VidexFont`** (new): a synthetic 256×8 char ROM (the `Apple2Font.Fallback` shape — `$20` blank, every
  printable code a deterministic countable-ink glyph) so the render gate is **asset-free / always-runs** (the
  real char ROM is the PR-O asset). **The `$C800` mapper is the 2nd `AddressSpace.Remap` consumer** (after the
  Language Card): `Realize` captures the program bus and Remaps `$C800-$CBFF` → firmware ROM (read-only) +
  `$CC00-$CDFF` → VRAM bank 0 (writable RAM — the guest's hot character writes ride the fastmem fast path, the
  ADR 0009 fast-RAM intent realized through `Remap`-to-RAM since **`IFastMemoryProvider` is NOT in `src/`**, ADR-
  designed/never-shipped like `TimingTier`); `SelectBank` re-points `$CC00` to the new 512-byte bank array (the
  live guest-write link held — the guest writes the mapped array, the render reads the same array, no copy).
  **`Apple2Iou`** delegates `$C0B0-$C0BF` to an optional Videx (the `$C08x`→LC / `$C0Ex`→Disk II pattern — a write
  rides `ApplyAnyAccessSideEffect`, a read rides `BusValue`, `TryPeek` short-circuits to open-bus 0, **peek-free**:
  a debugger peek never programs the CRTC, switches banks, or raises `ActiveChanged`); the new 4-arg ctor
  `(state, lc, disk2, videx)` (the 3-arg chains to it), the bare board (`_videx == null`) byte-for-byte unchanged.
  **`Apple2Board.SpecWithVidex`** carves `$C000-$CFFF` so the `$C800-$CBFF` Videx firmware slot (Mmio, the
  factory Realizes the card) + the `$CC00-$CDFF` VRAM (Ram, the Videx Remaps to bank 0) windows are validator-
  clean and the IOU still owns `$C000`. **Pre-merge review (focused on the `$C800` Remap live-link + page-
  alignment, the IOU peek-free + bare-board regression, the board carve validator-cleanliness, the render bounds/
  bit-order/un-fakeability, and the `ReadReg` index fix) found 1 HIGH (fixed):** the IOU `$C0Bx` delegate dropped
  the written value — the `Access(offset, isRead)` signature, copied verbatim from the value-agnostic LC/Disk II
  soft switches, carried no write byte, so a real 6502 `STA $C0B0`/`STA $C0B1` always passed `0x00` and the CRTC
  could never be programmed through the address bus (the render gate passed only because `Program80x24` called
  `videx.Write(...)` DIRECTLY, bypassing the IOU path). **Fix:** threaded `(byte)value` through `Apple2Iou.Write`
  → `ApplyAnyAccessSideEffect` → `VidexVideoterm.Access(offset, isRead, writeValue)` (the LC/Disk II `Access` calls
  + the peek-free short-circuit untouched) + a new **un-fakeable end-to-end bus-path regression test**
  (`Programming_the_CRTC_through_the_bus_C0B0_C0B1_sets_the_geometry`) that programs a NON-default 40×20 geometry
  via `bus.Write8($C0B0/$C0B1)` and asserts 280×180 — a dropped value falls back to the 80×24 default, which the
  assertion rejects. **1 MEDIUM surfaced to the owner, deliberately NOT fixed (a plan-acknowledged PR-O
  deferral):** the `$C0B8-$C0BF` bank-select decode `(o >> 2) & 0x03` reaches only banks 2/3 via the hardware
  path (the gate selects banks via `SelectBankForTest`); the plan defers the exact decode to PR-O ("refine against
  the real CP/M driver / Videx firmware 2.4") — left as-is until the asset confirms the real bank cadence. The
  `ReadReg` plan-literal `_crtc[_crtcAddr & 0x1F % 18]` (an operator-precedence bug) was implemented correctly as
  `_crtc[_crtcAddr % 18]`. **The un-fakeable gates** (asset-free, always-run): `Crtc_programming_yields_80x24_
  geometry` (560×216), `Vram_of_known_codes_renders_structural_ink_through_the_synthetic_char_rom` (a dead render
  is all-off — unfakeable), `Selecting_a_vram_bank_remaps_the_CC00_window...` (the 2nd `Remap` consumer, the live
  bank link), `SpecWithVidex_validates_and_builds...` (the carve is validator-clean), and the capstone
  `DisplayMultiplexer_switches_to_the_Videx_80col_when_it_signals_active` (a `DisplayMultiplexer` over
  `[apple40, videx80]` follows `ActiveChanged` — Apple-40 → Videx-80 → back, firing `FrameReady` + re-pulling at
  the new 560×216 geometry, rendering the Videx's inked frame), plus the new bus-path CRTC gate. **UAT** (no
  browser MCP; HTTP+WS frame level, the IOU hot-path is shared by every Apple board): the live server boots clean
  on `:5252`, `GET /`+`/app.js`+`/index.html` 200, **zero server errors/exceptions**, the `WebServerSmokeTests`
  UAT (serves the canvas client, WS upgrades, `ST` text frame leads, binary `FB` frame streams) green — the IOU
  `$C0Bx` delegate did not regress the Apple/Spectrum/demo fall-through. Gate: full suite **7220 passed / 0 failed
  / 5 skipped** (the 7211 post-PR-M baseline + 9 new PR-N tests, purely additive), warning-clean. **The Videx
  80-col card is ready for PR-O's CP/M-on-Videx capstone** (deps K, N ✅) — the "usable 80-column CP/M" deliverable.
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
