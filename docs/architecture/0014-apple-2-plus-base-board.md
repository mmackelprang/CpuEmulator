# ADR 0014 — The Apple ][+ base board: memory map, the `$C0xx` soft-switch seam, video/keyboard/speaker, the Language Card, and Disk II

> **Status:** PROPOSED (Architect phase, Apple ][+ arc). No implementation now — this ADR maps the **base** Apple ][+
> hardware onto the shipped `BoardSpec` / `BoardMachineFactory` / `IPeripheral` + SP0-contract model so the Planner can
> decompose it into PRs and the Designer knows the surfaces. The **dual-CPU Z80 SoftCard board** is split into its own
> ADR (**0015**) given its weight; the **CP/M deliverable** (Videx 80-column second-display seam + asset/licensing
> posture) is **0016**. See "Proposed ADR split" at the end of this file's sibling report.
> **Date:** 2026-06-20
> **Deciders:** Mark (owner). Drafted autonomously by Claude Architect.
> **Reads as ground truth:**
> - `docs/research/apple-2-plus-architecture-analysis.md` — the base-machine hardware reference (memory map, `$C0xx`
>   switches, the verified hi-res/text address formulas, Language Card, Disk II GCR/LSS, boot/RESET). Every hardware
>   fact below traces to that doc; section references are to it unless noted.
> - `docs/research/apple-2-plus-z80-softcard-cpm-analysis.md` — the SoftCard companion (consumed by ADR 0015/0016).
> **Supersedes / relates to:**
> - **The shipped Machine model** (`src/CpuEmulator.Machines/`): `BoardSpec`, `BoardSpecValidator`,
>   `BoardMachineFactory`, `CpuKind`→`CpuCoreFactory`. This is the abstraction the arc extends — **not** ADR 0010's
>   JSON manifest loader, which remains deferred SP1+ vision. The Apple ][+ is authored as a C# `BoardSpec` exactly as
>   `SpectrumBoard` is.
> - **ADR 0009** (device↔JIT contract): Decision 1 (fastmem-RAM vs MMIO split), Decision 2 (`AddressSpace.Remap` fires
>   page-level JIT invalidation — **the Language Card and Disk II soft-switches are the first shipping consumers of a
>   seam ADR 0009 designed but never built**), Decision 3 (`TimingTier` coarse/fine).
> - **ADR 0013** (per-bank block specialization, `(PC, BankConfigId)` keys) — directly relevant to the Language Card,
>   which alternates ROM/RAM banks at `$D000–$FFFF`; flagged as the M6-class optimization the LC mapper can later opt into.
> - **The ZX Spectrum precedent** (`SpectrumBoard`, `SpectrumUla`, `SpectrumSurface`): the closest existing pattern —
>   one chip facing the guest as `IPeripheral` and the host as `IDisplayDevice`+`IKeyboardSink`+`IAudioSink`, reading
>   main RAM via an injected `IAddressSpace`, raising a frame interrupt from a scheduler tick. The Apple video peripheral
>   is the same shape.
> - **ADR 0002** (flat 256-byte page table, 8..24 `addressBits`) — the Apple ][+ is a clean 16-bit (`addressBits: 16`)
>   little-endian board; every region below is page-aligned and page-multiple by construction.

---

## 1. Context

### 1.1 The target, and why it fits the shipped model

The Apple ][+ is a 6502 at ~1.0205 MHz (master 14.31818 MHz ÷ 14, with a per-scan-line "long cycle" stretch; §1) with a
fixed 64 KiB address space: 48 KiB RAM `$0000–$BFFF`, an I/O + slot-ROM band `$C000–$CFFF`, and 12 KiB system ROM
`$D000–$FFFF` (Applesoft + Monitor). We already own a cycle-/parity-gated 6502 core on both tiers (M1/M2/M6). So — exactly
as the Spectrum was — **this arc is board + peripherals, not new-CPU work.** The base board is a `BoardSpec` with a
`CpuKind.Mos6502`, three classes of region, and a small set of peripheral slots, compiled by `BoardMachineFactory` into
the same runnable `Machine` every other board produces.

The Apple ][+ is, however, materially harder than the Spectrum in four specific ways, and those four are the substance of
this ADR:

1. **The `$C0xx` band is not one device — it is a decode seam** over dozens of side-effecting soft switches (video mode,
   keyboard, speaker, Language Card, slots). On the ][+ the video and LC switches toggle on **any access, read OR write**
   (unlike the IIe's read/write polarity; §3) — a decode rule the peripheral must honor exactly.
2. **The video peripheral is non-trivial**: text (40×24, GBASCALC interleave), lo-res (40×48), and hi-res (280×192 with
   the verified `addr(y)` formula and the bit-7 half-pixel/artifact-color model; §4). It reads main RAM for scanout — no
   VRAM of its own — exactly like the ULA.
3. **The Language Card is run-time bank switching** of `$D000–$FFFF` between ROM and two `$D000` RAM banks, write-enabled
   only by **two consecutive reads** of an odd `$C08x` (a pre-write flip-flop; §7). This is ADR 0009 Decision 2's
   `Remap`-driven bus change — the first one the project ships.
4. **Disk II is a nibble-level device** (the LSS sequencer, 6-and-2 GCR, `.woz`-preferred fidelity; §8) layered over the
   SP0 `IBlockDevice` storage seam — not a logical-sector controller.

### 1.2 What the shipped code gives us (verified, not assumed)

- **`BoardSpec` already expresses the three region classes the Apple needs.** `RegionKind.Ram`/`Rom`/`Mmio` +
  `PeripheralSlot` over an `Mmio` hole is exactly the Spectrum's ULA-on-`Io` pattern, but on the **Program** bus (the
  Apple has no separate I/O port space — its I/O is memory-mapped at `$C0xx`, so `IoAddressBits` stays `0` and the slots
  are `PeripheralSpace.Program` in `Mmio` regions). `BoardSpecValidator` already checks overlap, page-alignment,
  slot-in-Mmio, IRQ-wired, ROM-size, and vector-patch-in-ROM (`BoardSpecValidator.cs`).
- **The video-reads-main-RAM pattern is proven.** `SpectrumUla` binds `context.Space(AddressSpaceKind.Program)` in
  `Realize` and reads the live RAM the guest wrote (`SpectrumUla.cs:60`), walking the non-linear screen address in
  `RenderInto`. The Apple video peripheral does the identical thing with the Apple's (different, also non-linear) address
  math.
- **`AddressSpace.Remap` does NOT exist yet.** ADR 0009 Decision 2 + §3.2 designed `Remap`/`RemapPeripheral` +
  `IMapInvalidationListener` + `BlockCache.InvalidatePages`, but a grep confirms no `Remap` method is shipped
  (`AddressSpace.cs` has `MapMemory`/`MapPeripheral` only; the ROADMAP lists per-bank work as `[candidate]`). **The
  Language Card forces this to be built.** It is the single largest *framework* change the base board requires, and it is
  pre-designed in ADR 0009 — this ADR confirms the Apple ][+ as the trigger and pins the exact shape (Decision 4).
- **The interpreter is the oracle; partial JIT emit is a pure perf dial.** Every Apple peripheral can be validated on the
  interpreter tier first; the un-fakeable gates (below) run tier-agnostic, with the ROM-boot gate on both tiers (the
  Spectrum's exact discipline).

---

## 2. Decisions

### Decision 1 — The base ][+ as a `BoardSpec`: three region classes, slot-keyed `$C0xx` peripherals on the Program bus

The Apple ][+ ships as a C# `BoardSpec` (named `"apple2plus"`), authored like `SpectrumBoard.Spec(...)`. The skeleton
(addresses are hardware-exact; the named peripherals are Decisions 2–6):

```csharp
// src/CpuEmulator.Machines/Apple2Board.cs  (shape, not full source)
return new BoardSpec("apple2plus", CpuKind.Mos6502, AddressBits: 16,
    Memory:
    [
        new MemoryRegion(0x0000, 0xC000, RegionKind.Ram),                 // 48 KiB main RAM $0000-$BFFF
        new MemoryRegion(0xC000, 0x1000, RegionKind.Mmio),                // I/O + slot ROM $C000-$CFFF (the soft-switch + slot band)
        new MemoryRegion(0xD000, 0x3000, RegionKind.Rom, systemRom),      // Applesoft+Monitor $D000-$FFFF (12 KiB)
    ],
    Peripherals:
    [
        new PeripheralSlot("iou",  iou,  0xC000, 0x0100),                 // the $C0xx soft-switch decoder (Decision 2)
        new PeripheralSlot("video", video, /* $C050-$C057 decode handled inside the IOU; see Decision 2 */ ...),
        new PeripheralSlot("lc",   lcMapper, 0xC080, 0x0100),            // Language Card bank-switch ports (Decision 4)
        new PeripheralSlot("disk2", disk2, 0xC600, 0x0200),             // slot 6: $C600 boot ROM + $C0Ex regs (Decision 6)
        // (slot ROM windows $C100-$C7FF + the $C800 expansion band: Decision 5)
    ],
    Irq: IrqWiring.None,             // the bare ][+ has no interrupt source; Disk II is polled. (Decision 6 note.)
    Reset: ResetConfig.None,         // the system ROM image carries its own $FFFC/$FFFD vector → $FA62 (§9)
    IoAddressBits: 0);               // memory-mapped I/O only; no Z80-style port space
```

Two structural points the validator already enforces and the author must respect:

- **`$C000–$CFFF` is one `Mmio` region** (a hole) that several peripheral slots fill. `BoardSpecValidator` requires every
  Program slot to be page-aligned (256 B) and fully contained in an `Mmio` region (`slot-not-in-mmio`,
  `slot-misaligned`). The Apple's soft switches are sub-page (`$C050` is one of 256 bytes in the `$C000` page), so the
  **page granularity forces a decode-aggregator peripheral** (Decision 2) rather than one slot per switch — the same
  reason `SimpleUart`/`IntervalTimer` do internal `offset & mask` sub-page decode.
- **No I/O port space.** Unlike the Spectrum (`IoAddressBits: 16`, ULA on the `Io` bus), the Apple is pure
  memory-mapped. `IoAddressBits` stays `0` and every peripheral is `PeripheralSpace.Program`. This is already a supported
  configuration (every pre-Spectrum board) — no framework change.

**Rationale.** It reuses the proven `BoardSpec` path verbatim; the Apple's only novelty vs. the Spectrum at the
*composition* layer is "memory-mapped I/O on the Program bus" (already supported) and "several slots over one Mmio
region" (already supported and validated). The hard parts are peripheral *behavior* (Decisions 2–6), which is correctly
code, not config (ADR 0010 Decision 1's bright line).

**Alternatives considered.**
- **(A) One `PeripheralSlot` per soft-switch group** (a `video` slot at `$C050`, a `keyboard` slot at `$C000`, …).
  *Rejected* — the 256-byte page granularity (`AddressSpace.PageSize`) makes every switch share the `$C000` page; you
  cannot map two peripherals into the same page (`EnsureRangeUnmapped` throws). The switches must be decoded by one
  peripheral that owns the `$C000` page and dispatches internally.
- **(B) Model `$C0xx` as RAM the guest writes, polled by devices.** *Rejected* — the switches are **side-effecting on
  access** (a *read* of `$C050` turns graphics on); they are MMIO by definition, not memory. A fastmem-RAM region cannot
  trap a read (ADR 0009 Decision 1's accepted constraint).

**Consequences.** *Good:* zero framework change at the composition layer; the board reads like the Spectrum.
*Bad/accepted:* the `$C000` page is one fat decoder peripheral (Decision 2) — a concentration of decode logic, mitigated
by keeping each switch group's behavior in its own collaborator object the decoder delegates to.

### Decision 2 — The `$C0xx` soft-switch decoder: one `Apple2Iou` peripheral owning the `$C000` page, with **any-access (read-OR-write) toggle** semantics, delegating to collaborator devices

A single peripheral — `Apple2Iou` (the I/O Unit) — maps the `$C000` page (`$C000–$C0FF`) and decodes every soft switch
by `offset`, dispatching to collaborators: the **video** state (mode/page flags), the **keyboard** latch, the
**speaker** toggle, and forwarding `$C080–$C08F` to the **Language Card** mapper (Decision 4) and `$C0E0–$C0EF` to
**Disk II** (Decision 6). It implements `IPeripheral.Read`/`Write`/`TryPeek`.

The load-bearing ][+ correctness rule: **the video and Language-Card soft switches toggle on *any* access — read OR
write (§3).** So `Read(offset)` and `Write(offset, …)` for those address groups perform the **same** state change; the
difference is only that `Read` returns a bus value and `Write` does not. `TryPeek` (the debugger's side-effect-free
path) must **not** toggle — it returns the would-be read value without changing state. This is the inverse of the IIe
and a classic source of emulator bugs; the decoder encodes it explicitly:

```csharp
// Apple2Iou.Read / Write share a single SwitchAccess(offset) that applies any-access side effects;
// TryPeek calls a parallel PeekValue(offset) that has NONE. (Shapes, not full impl.)
public uint Read(uint offset, AccessWidth w)  { ApplyAnyAccessSideEffect(offset); return BusValue(offset); }
public void Write(uint offset, AccessWidth w, uint v) { ApplyAnyAccessSideEffect(offset); WriteSideEffect(offset, v); }
public bool TryPeek(uint offset, out byte value) { value = (byte)BusValue(offset); return true; } // NO side effect
```

Switch groups the decoder owns (addresses from §3; `,X` notation is slot-relative — Disk II is slot 6 → `$C0Ex`):

| Group | Addresses | Access semantics | Delegate |
|---|---|---|---|
| Video mode | `$C050–$C057` (TXTCLR/TXTSET/MIXCLR/MIXSET/LOWSCR/HISCR/LORES/HIRES) | **any access** toggles | video state (Decision 3) |
| Keyboard | `$C000` (read: bit7=strobe, bits6–0=code), `$C010` (clear strobe) | read-driven | keyboard latch (Decision 3) |
| Speaker | `$C030` | **any reference** toggles the 1-bit flip-flop | speaker (Decision 3) |
| Language Card | `$C080–$C08F` | **any access**; 2-consecutive-reads write-enable | LC mapper (Decision 4) |
| Disk II (slot 6) | `$C0E0–$C0EF` | sequencer-defined | Disk II (Decision 6) |

**Speaker double-toggle caveat (§3/§6):** a 6502 *write* instruction performs a read-before-write at the bus, so an
`STA $C030` toggles the speaker **twice**. The decoder models the speaker at the *bus-access* level (each `Read`/`Write`
call is one access) so the double-toggle emerges naturally — but the board author must ensure the 6502 core's
read-modify-write bus pattern actually issues that dummy read (it does: the core is cycle-exact). This is recorded as a
**build-time verification item**, not a new mechanism.

**Rationale.** One decoder owning the page is forced by the 256-byte mapping granularity (Decision 1(A)). The
any-access/peek-free split is the ][+'s defining I/O quirk and the single most bug-prone rule; making it a structural
invariant of the decoder (one `ApplyAnyAccessSideEffect`, called from both `Read` and `Write`, absent from `TryPeek`)
prevents the whole class of "the debugger changed the video mode by looking at it" and "a read didn't switch graphics on"
bugs.

**Alternatives considered.**
- **(A) Read-only and write-only side effects (the IIe model).** *Rejected* — wrong for the ][+ (§3 is explicit: any
  access). Modelling IIe semantics on a ][+ board would fail real software that switches modes via a read.
- **(B) Let each collaborator map its own page.** *Rejected* — impossible at 256-byte granularity (Decision 1(A)).

**Consequences.** *Good:* the ][+'s defining I/O behavior is correct by construction and peek-safe for the monitor.
*Bad/accepted:* `Apple2Iou` is a hub with many collaborators — high fan-in, mitigated by delegating behavior to small
per-group objects (the decoder is dispatch, not logic).

### Decision 3 — Video, keyboard, speaker: an `Apple2Video` peripheral facing the host as `IDisplayDevice`, plus keyboard `IKeyboardSink` and speaker `IAudioSink` — the ULA pattern, reading main RAM (no VRAM)

The video/keyboard/speaker triad mirrors `SpectrumUla` exactly: **one host-facing chip (`Apple2Video`) reads main RAM
for scanout, owns no VRAM, and implements the SP0 host contracts**, while its *guest-facing* control (the mode/page
switches, the keyboard latch, the speaker toggle) lives on the `Apple2Iou` decode path (Decision 2), which mutates the
video/keyboard/speaker **state** the chip reads. Concretely:

- **`Apple2Video : IPeripheral, IDisplayDevice`** — binds `context.Space(AddressSpaceKind.Program)` in `Realize` (reads
  `$0400–$07FF`/`$0800–$0BFF` text/lo-res and `$2000–$3FFF`/`$4000–$5FFF` hi-res from live RAM), schedules a ~60 Hz
  frame tick that raises `FrameReady` (and, for the base ][+, **raises no interrupt** — the bare machine has no vblank
  IRQ; the frame tick is purely the host-present trigger). `RenderInto(Span<uint> rgba)` walks the *current* mode
  (driven by the IOU's mode/page flags) and produces RGBA. The chip does its own palette/artifact-color lookup so the
  surface stays a dumb blitter (the `IDisplayDevice` contract).
- **The verified address math goes in `RenderInto`** (the un-fakeable render gate proves it):
  - Hi-res: `addr(y) = 0x2000 + (y/64)*0x28 + (y%8)*0x400 + ((y/8)&7)*0x80` (page 1; `+0x4000` page 2). §4 verified this
    bijective over y=0..191 and **refuted** the swapped-stride variant — implement *this* formula, with a regression
    test that asserts the landmark rows (y=0→`$2000`, y=1→`$2400`, y=8→`$2080`, y=64→`$2028`, y=191→`$3FD0`).
  - Text/lo-res: the GBASCALC row→base mapping (24 row bases `$400,$480..$780` / `$428..$7A8` / `$450..$7D0`), with the
    8-byte screen-holes at `$78–$7F` of each 128-byte block left unread (a consequence of the 3-row interleave, §4).
  - Hi-res bit-7 half-pixel/artifact model: low 7 bits = 7 pixels; bit 7 = a one-14 MHz-cycle delay (~90° NTSC phase)
    that shifts green/purple → blue/orange. The renderer models the 12°-offset NTSC artifact palette (§4). **The
    artifact-color fidelity dial is a build-time follow-up** (Decision 8): ship a correct monochrome + basic-artifact
    renderer first; the full NTSC-phase model is a later quality increment, not a blocker.
- **Keyboard: an `Apple2Keyboard : IKeyboardSink`** collaborator. The IOU's `$C000` read returns its latch (bit 7 =
  strobe, bits 6–0 = code); `$C010` clears the strobe. The ][+ is **uppercase-only** with a non-standard set —
  `PostKey` maps portable `KeyCode`s to the ][+ code set (the analogue of `SpectrumKeyMatrix`). The host pushes keys;
  the chip owns the native mapping (the `IKeyboardSink` contract).
- **Speaker: an `Apple2Speaker : IAudioSink`** collaborator. `$C030` toggles a 1-bit flip-flop (any access, Decision 2).
  The PCM reconstruction is the Spectrum beeper's exact approach (§6): log the cycle timestamp of every toggle, rebuild
  the 1-bit waveform, low-pass + resample to host PCM (the double-toggle on write opcodes falls out of bus-access-level
  modelling). Reuse the `SpectrumUla` beeper resampler shape (`RenderAudio`, the toggle log, level-carry across frames).

**Composition note (an SP0/Spectrum lesson to carry forward):** `SpectrumMachine.Build` reveals an awkwardness — the ULA
must be the *same instance* mapped on the Io slot AND handed to the surface, and it binds RAM in `Realize`. The Apple has
the **same shape with more collaborators** (the IOU and the video chip share the same video-mode state). The Planner
should wire it the Spectrum way: construct the collaborators, build the `BoardSpec` referencing them, build the
`Machine` (which `Realize`s them over the built program space), then hand the *same* `Apple2Video` instance to the
surface as the `IDisplayDevice`/`IAudioSink` and `Apple2Keyboard` as the `IKeyboardSink`. **The shared video-mode state
between the IOU (writer) and the video chip (reader) should be one small mutable object both hold a reference to** — not
duplicated — so a `$C057` HIRES access is visible to the next `RenderInto` with no plumbing.

**Rationale.** This is the proven Spectrum pattern; the only deltas are the Apple's (verified) address formulas and the
multi-mode renderer. Keeping the chip VRAM-less and reading live RAM matches the hardware (the Apple video circuitry
reads DRAM during φ2-low; §1) and the snapshot-at-present semantics ADR 0009 Decision 1 endorses (`RenderInto` at
`FrameReady` reads a coherent guest-written framebuffer).

**Timing tier (ADR 0009 Decision 3):** the base ][+ display is **`Coarse`** — a 60 Hz snapshot is correct for text/
lo-res/hi-res in the common case (no per-scanline register reprogramming on the bare ][+; the IIe/double-hi-res raster
tricks are out of scope, §4 caveat). Mid-frame mode switches (a game that flips MIXSET mid-screen for a text status bar)
are the one case that would want `Fine`; recorded as a build-time follow-up — ship `Coarse`, measure, escalate the
display to `Fine` only if a target title visibly tears. (This is exactly ADR 0009 Open Question 6: one `IDisplayDevice`
spans both tiers via `ITimingSensitive`.)

**Alternatives considered.**
- **(A) Give the video chip its own VRAM and trap guest writes to `$0400`/`$2000`.** *Rejected* — those are ordinary RAM
  addresses the guest also uses for non-video data and code; trapping them is the MMIO tax ADR 0009 Decision 1 forbids,
  and it is wrong (the regions are plain RAM). Read live RAM at present, like the ULA.
- **(B) Render in the IOU.** *Rejected* — conflates the guest-facing decode (IOU) with host-facing scanout (video chip);
  the Spectrum keeps them in one object only because the ULA *is* one chip. The Apple's decode (`$C0xx`) and scanout are
  genuinely separable; separating them keeps each testable.

**Consequences.** *Good:* the render/keyboard/beeper gates are un-fakeable (assert real RGBA / latch bytes / PCM, no ROM
needed) — the Spectrum's exact testing posture. *Bad/accepted:* the IOU↔video shared-state coupling (one object both
reference) is a small mutable-state seam to document; the alternative (events) is overkill for a few flags.

### Decision 4 — The Language Card: a code "mapper" peripheral over `$C080–$C08F` that calls `AddressSpace.Remap` — and this arc **builds** the ADR-0009-Decision-2 remap seam (its first shipping consumer)

The Language Card banks `$D000–$FFFF` between system ROM and 16 KiB of card RAM (two 4 KiB banks at `$D000` plus shared
`$E000–$FFFF`; §7). It is **run-time bank switching driven by guest access to `$C080–$C08F`** — precisely ADR 0009
Decision 2's "a bus remap is page-level SMC" case, and the **first** time the project ships it. The LC is modeled as a
code **mapper** peripheral (`Apple2LanguageCard : IPeripheral`) that owns the `$C08x` ports (via the IOU's delegation,
Decision 2) and, on each access, computes the new `$D000–$FFFF` mapping and calls `AddressSpace.Remap`.

**This ADR therefore confirms the Apple ][+ as the trigger to BUILD the remap seam ADR 0009 §3.2 designed but never
shipped.** The seam to add to `CpuEmulator.Core` (exactly ADR 0009 §3.2 — restated here so the Planner has one place):

```csharp
// AddressSpace (additive — the bank-switch primitive ADR 0009 Decision 2 specified, not yet built):
public void Remap(uint start, byte[] backing, bool writable);          // re-point a mapped range to RAM/ROM
public void RemapPeripheral(uint start, uint length, IPeripheral p);   // re-point a range to MMIO
internal void AddMapInvalidationListener(IMapInvalidationListener l);  // the JIT registers; Core does not ref Jit

// Core defines; the JIT implements (preserving the Core→Jit dependency direction of TryGetDirectAccess):
public interface IMapInvalidationListener { void OnRemap(int firstPage, int pageCount); }
// JIT side: BlockCache.InvalidatePages(firstPage, pageCount) factored from InvalidateIfDirty; Fastmem re-classifies the range.
```

The LC mapper's state machine (the exact ][+ rules, §7):

- **`$C080–$C08F` decode** (the standard LC truth table): bit 0 of the offset selects bank 1 vs bank 2 at `$D000`; bits
  1–0 together select read-ROM/read-RAM and the write-enable arming. **Write-enabling LC RAM requires two *consecutive
  reads* of an odd `$C08x`** — a single read does not arm it (the 74LS175 pre-write count flip-flop). The mapper holds
  the arm counter and only flips `$D000–$FFFF` to writable RAM after the second consecutive qualifying read.
- **On a state change**, the mapper calls `Remap($D000, …)` (and `$E000`) to point the `$D000–$FFFF` pages at either the
  system ROM image, LC-RAM bank 1, or LC-RAM bank 2 — and `Remap` fires `OnRemap`, so the JIT re-classifies those pages
  in `Fastmem` and evicts cached blocks decoded from them (the LC commonly runs **code** out of the banked RAM — DOS,
  ProDOS, integer BASIC — so stale-block invalidation is mandatory, not theoretical).
- **64K-vs-48K presence detection is a write-test to `$D000` LC RAM** (§7), which works once the LC RAM is mapped — no
  special handling beyond the remap making `$D000` writable.

**M6 interaction (ADR 0013):** the LC alternates a *small fixed set* of bank configurations (ROM, RAM-bank-1, RAM-bank-2
× read/write). ADR 0013's `(PC, BankConfigId)` per-bank block specialization is the natural optimization if LC
bank-thrash ever shows in a profile — the LC mapper assigns a `BankConfigId` per configuration and the JIT keeps
per-config compiled blocks instead of evicting on every switch. **Flagged as a deferred optimization, not a base-board
requirement** — the page-precise evict-on-remap (the seam above) is correct and sufficient to ship; ADR 0013 is the
speed dial if needed.

**Rationale.** The LC *is* the canonical bank-switch case ADR 0009 designed for; building `Remap` here (rather than
inventing an Apple-specific path) lands a framework primitive every future banked machine (C64 PLA, more Apple cards,
PC EMS) reuses — and it is already designed, reviewed, and owner-accepted in ADR 0009. Modeling the LC as a code mapper
(not config) is ADR 0010 Decision 1's bright line: run-time bus remapping is behavior.

**Alternatives considered.**
- **(A) Pre-map all banks into a wider flat space.** *Rejected* — ADR 0009 Decision 2(D): the 6502 sees 64 KiB; the LC
  multiplexes 16 KiB of RAM + 12 KiB ROM through the *same* `$D000–$FFFF` window. They genuinely overlap in the guest's
  address space; a flat map cannot represent it.
- **(B) Full block-cache flush on each LC switch (ADR 0009 Decision 2(C) stopgap).** *Rejected as the shipping shape* —
  the LC switches often enough (every DOS call crossing the ROM/RAM boundary) that full-flush thrash is the exact M2-ii
  pathology; ADR 0009 explicitly recommends the page-precise hook from the start since the machinery exists. Build the
  page-precise hook.
- **(C) Defer the LC (ship a 48 KiB ][+ only).** *Rejected* — the LC is required for the SoftCard/CP/M arc (ADR 0015:
  the Z80 translation maps `$B000–$DFFF` onto LC bank 2 + ROM/LC `$F000`), and for DOS/ProDOS — it is not optional for a
  "full machine including Disk II." It can, however, be **sequenced after** the bare-board video/keyboard/disk PRs (see
  the PR decomposition) since the remap seam is its own substantial piece.

**Consequences.** *Good:* the project gains the long-designed `Remap` primitive, validated by a real consumer; the LC is
correct and JIT-safe. *Bad/accepted:* `Remap` is a new `Core`↔JIT seam with teeth (it touches `Fastmem` + block
eviction) — but it reuses the proven per-page SMC eviction and is gated behind a method no current device calls (ADR
0009 §4 reversibility). The interpreter tier needs no listener (it re-reads the live page table every access) — so the
LC is shippable and gateable on the interpreter first, with the JIT listener as a parallel, separately-gated PR.

### Decision 5 — Slot ROM + the `$C800` expansion band: a static slot-ROM map plus a small banked-expansion mapper, slot 6 reserved for Disk II

The `$C100–$C7FF` band is 256 B of peripheral-card ROM per slot (slot N at `$CN00`), and `$C800–$CFFF` is a **shared 2
KiB expansion-ROM window** banked to whichever card was last selected — enabled by any `$CnXX` access, reset by a
`$CFFF` access (§2). For the **base ][+** the only card present is the Disk II in slot 6, so:

- **`$C600` (slot 6 ROM)** is the Disk II boot ROM (Decision 6), mapped as part of the Disk II peripheral's slot window.
  Cold boot scans slots 7→1 for the Disk II signature (`$Cn01=$20,$Cn03=$00,$Cn05=$03,$Cn07=$3C`) and `JMP ($Cn00)` →
  `$C600` for slot 6 (§9) — so the boot ROM's first bytes must carry that signature.
- **The `$C800–$CFFF` expansion band** is a banked window. On the bare ][+ it matters only when the Videx card is
  present (ADR 0016: the Videx firmware lives at `$C800–$CBFF` and its VRAM banks into `$CC00–$CDFF`). For the base
  board with only Disk II (which uses no `$C800` expansion ROM), the band can be left unmapped/open-bus. **The
  `$C800`-bank mapper is therefore deferred to ADR 0016** (it is first needed by the Videx); the base board does not
  build it. This keeps the base-board scope clean and pushes the expansion-bank machinery into the card that needs it.

**Rationale.** Slot ROM is a static map (`$C600` is just ROM at a fixed base — config, not behavior); the `$C800`
expansion *bank* is behavior (an access-driven remap) but is only exercised by the Videx, so it belongs with the Videx
(ADR 0016) under the same `Remap` seam Decision 4 builds. Splitting it out keeps the base board free of machinery no
base-board peripheral uses (YAGNI).

**Consequences.** *Good:* the base board maps only what slot 6 needs; the expansion-bank complexity rides with the Videx.
*Bad/accepted:* ADR 0016 inherits the `$C800` mapper — noted there as a dependency.

### Decision 6 — Disk II: a nibble-level controller (`Apple2DiskII : IPeripheral`) over the SP0 `IBlockDevice` storage seam, `.woz`-preferred, modeling the LSS sequencer

Disk II ships as a code peripheral `Apple2DiskII : IPeripheral` mapping the slot-6 window (`$C600` boot ROM +
`$C0E0–$C0EF` sequencer/stepper/motor soft switches), **layered over SP0's `IBlockDevice`** for raw storage (the SP0
seam: `SectorSize`/`SectorCount`/`ReadSector`/`WriteSector`, `IBlockDevice.cs`). Per §8 and the SP0 contract comment
("controllers + image formats are SP1+"), this is the first real disk **controller** the project ships, and it sits on
top of the storage seam exactly as the SP0 contract anticipated.

The fidelity decision (§8): **model the LSS sequencer + the nibble stream, not logical sectors** — Apple copy
protection routinely reads raw nibbles, half-tracks, and timing. The controller:

- Owns the `$C0E0–$C0EF` (slot-6) soft switches: stepper phases `$C0E0–$C0E7` (head movement), motor on/off
  `$C0E8`/`$C0E9` (with the **~1 second 556-one-shot motor-off delay**, scheduled via `IScheduler` — a real timing
  detail), drive 1/2 select, and the data/sequencer ports (`$C0EC`/`$C0ED` reset the sequencer + clear the latch,
  `$C0ED` = `$C08D,X` for slot 6, §8). The Q6/Q7 read/write-mode latches gate read vs. write vs. sense.
- Produces/consumes the **6-and-2 GCR** nibble stream: 256-byte sector → 342 6-and-2 bytes + 1 checksum = 343 (§8); the
  64 valid on-disk bytes (`$96`..`$FF`, MSB set, ≤2 consecutive zero bits). DOS 3.3 is 16-sector.
- **Image fidelity tiers (§8): `.woz` is the high-fidelity choice** — it stores a normalized exact-length per-track
  bitstream that loops and preserves protection timing/sync; `.dsk`/`.po` (logical sectors) are adequate only for
  non-protected disks (re-nibblize on the fly). **Decision: target `.woz` as the primary image format** (the controller
  reads a track bitstream), with a `.dsk`/`.po` adapter that re-nibblizes into a synthetic track for unprotected disks.

**Where `.woz` meets `IBlockDevice`:** `IBlockDevice` is a *logical-sector* (LBA) seam — it is the right backing for
`.dsk`/`.po` (LBA → file offset, as SP0's `DiskImage` does) but **not** for `.woz`'s track bitstream. So:

- For `.dsk`/`.po`: back the controller with an `IBlockDevice` (the SP0 `DiskImage`), re-nibblizing on read.
- For `.woz`: the controller needs a **track-bitstream** backing, not LBA sectors. **Recommendation:** add a thin
  parallel storage interface for nibble/bitstream images (`IFluxImage`-style: per-track bit arrays + bit length) that
  lives beside `IBlockDevice`, rather than forcing the bitstream through the LBA seam. The SP0 contract explicitly scoped
  image-format quirks to "the controller/adapter's concern (SP1+)" — `.woz` is exactly that. **Flagged for the Planner
  as a small new storage contract** (it does not change `IBlockDevice`; it sits alongside it, the same way `IBlockDevice`
  sits alongside `IDisplayDevice`).

**Interrupts:** Disk II is **polled** on the ][+ (the 6502 reads the data latch in a tight loop; the controller raises
no IRQ). So `IrqWiring.None` for the base board (Decision 1) — the controller's timing is the motor-off delay + the
read-latch cadence, both via `IScheduler`. The disk read loop is **timing-sensitive at the bit level**; the controller
declares `TimingTier.Fine` for its sequencer (ADR 0009 Decision 3) so the byte-arrival cadence the guest polls is
serviced at the right cycles. The P5/P6 boot-ROM sequencer internals (§ residual open item 4) are a build-time
follow-up; the boot hand-off (`$C600` via the signature scan) is covered (§9).

**Rationale.** Nibble-level fidelity is the only model that runs real Apple disks (copy protection is pervasive); `.woz`
is the format the protection-preservation community standardized on. Layering on the SP0 `IBlockDevice` for `.dsk`/`.po`
reuses the shipped seam; adding a bitstream seam for `.woz` is the minimal honest extension (you cannot fake a flux image
through an LBA interface). The motor-off delay and `Fine` sequencer timing are required for real disks to boot.

**Alternatives considered.**
- **(A) Logical-sector controller only (`.dsk` via `IBlockDevice`, no nibbles).** *Rejected as the target* (it cannot
  run protected disks) but **acceptable as the first PR** — a `.dsk`-only, sector-level Disk II that boots DOS 3.3 is a
  legitimate intermediate milestone that proves the boot path, deferring `.woz`/nibble fidelity to a follow-on PR. (See
  the PR decomposition: "Disk II (sector-level)" then "Disk II (.woz/nibble fidelity)".)
- **(B) Force `.woz` through `IBlockDevice`.** *Rejected* — a track bitstream is not LBA sectors; squeezing it through
  the sector seam loses exactly the timing/sync `.woz` exists to preserve.

**Consequences.** *Good:* a path to real-disk fidelity, reusing the SP0 storage seam for the common case; the boot path
is gateable early (sector-level) before nibble fidelity lands. *Bad/accepted:* a new bitstream storage contract for
`.woz` (small, additive, beside `IBlockDevice`); the LSS sequencer is genuinely intricate (the project's first `Fine`
non-display device).

### Decision 7 — Boot, RESET, and ROM sourcing: ROM-image-carried vectors, fetch-on-demand assets, documented-opcodes-first 6502

- **RESET/boot:** the system ROM image carries its own `$FFFC/$FFFD` reset vector (→ `$FA62`, §9), so `ResetConfig.None`
  — no `VectorPatch` (unlike a board whose ROM lacks vectors). The 6502 core's existing `Reset()` reads `$FFFC/$FFFD`
  (the shipped mechanic). The Autostart Monitor's cold/warm decision (`$FA85`), page-3 vectors (`BRKV`/`SOFTEV`/
  `PWREDUP`/`AMPERV`), and the slot-scan disk boot (§9) are all *guest ROM behavior* — the board provides the ROM and
  the RAM; no board-level mechanism is needed beyond mapping them.
- **ROM assets are fetched on demand, never vendored** — the established `tools/get-spectrum-rom.{sh,ps1}` pattern
  (cache root `${CPUEMULATOR_TESTVECTORS:-$HOME/.cache/cpuemulator/vectors}`, sanity-check the byte length, fail loud,
  ROM-dependent tests skip-with-note when absent). The base board needs: the **Apple ][+ system ROM** (Applesoft +
  Monitor, `$D000–$FFFF`), the **Disk II P5/P6 boot ROM** (`$C600`), and the **character-generator ROM** (text glyphs).
  **All are Apple copyright → user-supplied** (§10; established practice + case law). **Exact image inventory + sizes
  (esp. the char-gen ROM) is a residual open item** (base §-residual 2) — provide the fetch script with the canonical
  sources from the research and the expected length sanity-checks; the precise char-gen contents are a build-time
  follow-up. (The CP/M/Videx assets + their licensing caveat are ADR 0016.)
- **6502 opcode scope: documented-only first** (§10). Real ][+ third-party software (e.g. Ultima I) and copy protection
  rely on illegal NMOS opcodes, so a strictly-151-opcode core will fail *some* titles — but Apple's own ROMs/DOS are
  widely believed clean. **Decision: ship documented-only; treat illegal-opcode support as a later compatibility dial**
  (§10's recommendation). The cheapest first increment is the SKW/NOP-class (`$0C,1C,3C,5C,7C,DC,FC` — a real read,
  value discarded); the minimal LAX/SAX/etc. set is a build-time follow-up (base §-residual 1/3). This is a **6502-core**
  follow-on, orthogonal to the board, and the interpreter-as-oracle discipline means it can land later without reworking
  the board.

**Rationale.** This is the Spectrum's exact, owner-blessed asset posture, extended to the Apple's three ROMs. The
documented-first opcode call keeps the arc unblocked (the system ROM boots without illegals) while honoring the
no-vendored-assets and interpreter-oracle invariants.

**Consequences.** *Good:* clean licensing posture; the board boots on documented opcodes; assets cached like every other
fetched corpus. *Bad/accepted:* some third-party titles need illegal opcodes (a known, scoped, later dial); the char-gen
ROM inventory needs build-time confirmation.

### Decision 8 — Residual hardware items: ship sensible defaults, list them as build-time follow-ups (non-blocking)

The research left a handful of items unresolved that do **not** block the ADR; each gets a recommended default so the
Planner can sequence them as follow-ups, not blockers:

| Residual item (source) | Recommended default to ship | Escalate to a follow-up when |
|---|---|---|
| Exact char-gen ROM size/contents/legal (base §-res 2) | Fetch script with length sanity-check; a built-in fallback glyph set for the no-ROM gate | The text-render gate needs real glyphs |
| Minimal illegal-opcode set (base §-res 1/3) | Documented-only; SKW/NOP-class as the first add | A target title demonstrably needs an illegal op |
| NTSC artifact-color fidelity (§4) | Correct mono + basic 4-color artifact; defer full 12°-phase model | A target needs faithful artifact color |
| P5/P6 boot-ROM sequencer internals (base §-res 4) | Use the real boot ROM (fetched); model the LSS it drives | — (covered by fetching the boot ROM) |
| Long-cycle (every-65th-cycle stretch) timing (§1) | Base ~1.0227 MHz rate; skip the per-line stretch initially | Cycle-exact video timing is needed (rare for ][+) |
| Mid-frame mode-switch tearing → display `Fine` (§4) | `Coarse` 60 Hz snapshot | A target visibly tears |

**Rationale.** Architect decisions live forever; soft-pedaling is the source of regret — but these are *fidelity dials*,
not architecture. Each has a correct, shippable default and a clear escalation trigger, so the arc is not blocked on
research the build phase can close cheaply.

---

## 3. Consequences (cross-cutting)

**Good.**
- The base ][+ reuses the shipped `BoardSpec`/`BoardMachineFactory`/`IPeripheral`/SP0-contract path with **one** real
  framework addition — `AddressSpace.Remap` + the JIT invalidation listener (Decision 4), which is already designed and
  owner-accepted in ADR 0009 and benefits every future banked machine.
- The video/keyboard/speaker triad is the proven `SpectrumUla` pattern; its gates are un-fakeable and ROM-free.
- The arc lands a long-deferred primitive (`Remap`) against a real consumer, and the project's first disk **controller**
  on the SP0 storage seam the contract anticipated.

**Bad / accepted costs.**
- The `Apple2Iou` decoder is a high-fan-in hub (Decision 2) — mitigated by delegating behavior to per-group collaborators.
- A new bitstream storage contract is needed for `.woz` (Decision 6) — small, additive, beside `IBlockDevice`.
- Illegal opcodes, char-gen specifics, artifact-color fidelity, and the long-cycle stretch are deferred fidelity dials
  (Decision 8) — defaults ship; triggers are documented.

**Reversibility.** High. The board is data; the peripherals are opt-in `IPeripheral` components. The one seam with teeth
— `Remap` — reuses the proven per-page SMC eviction and is gated behind a method no current device calls (ADR 0009 §4).
The interpreter tier needs no remap listener at all, so the whole LC can ship + gate on the interpreter before the JIT
side lands.

---

## 4. Open questions

1. **`.woz` storage contract shape (Decision 6).** A track-bitstream seam (`IFluxImage`-style) beside `IBlockDevice`, or
   a richer `IBlockDevice` variant? Leaning a small separate interface (a flux image is not LBA sectors). Resolve at the
   `.woz`-fidelity PR; the sector-level Disk II (first PR) needs only the existing `IBlockDevice`.
   > **✅ OWNER DECISION (Coordinator session, 2026-06-20): full `.woz`/LSS fidelity UPFRONT** — no
   > sector-first staging. The flux-image/track-bitstream seam is built from the start; the `.dsk`/`.po`
   > adapter re-nibblizes into that same track-bitstream path. (Overrides the Architect's sector-first PR
   > sequencing recommendation; the Planner sequences the disk PRs woz-first. The seam shape above —
   > small separate `IFluxImage`-style interface beside `IBlockDevice` — is accepted.)
2. **Display `Coarse` vs `Fine` (Decision 3).** Confirm the base ][+ display ships `Coarse` (no per-scanline
   reprogramming on the bare machine) and escalates to `Fine` only for a specific mid-frame-mode-switch title. Confirm
   against the first target software at Planner time (ADR 0009 OQ6).
3. **`Remap` placement on `IAddressSpace` vs concrete `AddressSpace` (ADR 0009 OQ4, now forced live).** The LC mapper
   needs to call `Remap`; ADR 0009 left open whether `Remap` lives on `IAddressSpace` (broad) or the concrete bus
   (narrow). Building it here forces the call. **Recommend `IAddressSpace`** for uniformity with `Read8`/`Write8`/
   `MapMemory` (the existing surface every device already sees) — owner's call, but it must be settled to ship Decision 4.
   > **✅ OWNER DECISION (Coordinator session, 2026-06-20): `Remap` lives on `IAddressSpace`** (the
   > recommendation is accepted). Settles ADR 0009 OQ4; PR-A may proceed.
4. **Long-cycle timing (Decision 8).** Does any target ][+ title need the per-scan-line cycle stretch (§1)? Default no;
   confirm if a cycle-exact-video title is in scope.

---

*End of ADR 0014. The base Apple ][+ is a C# `BoardSpec` (`CpuKind.Mos6502`, 16-bit, memory-mapped I/O) reusing the
shipped Machine model: 48 KiB RAM + the `$C000` I/O hole + the `$D000–$FFFF` system ROM, with an `Apple2Iou` soft-switch
decoder owning the `$C000` page (any-access toggle, peek-free), an `Apple2Video` chip facing the host as `IDisplayDevice`
(+ keyboard `IKeyboardSink` + speaker `IAudioSink`) reading live main RAM with the verified hi-res/text address formulas
— the `SpectrumUla` pattern. The **Language Card** is a code mapper that **builds and is the first consumer of**
`AddressSpace.Remap` + the JIT invalidation listener (designed in ADR 0009 Decision 2, shipped here), with ADR 0013's
per-bank specialization as the optional speed dial. **Disk II** is a nibble-level controller (`.woz`-preferred, LSS
sequencer, `Fine` timing) over the SP0 `IBlockDevice` storage seam plus a small `.woz` bitstream seam. Assets (system
ROM, Disk II boot ROM, char-gen ROM) are fetch-on-demand (the `get-spectrum-rom` pattern), documented-opcodes-first.
Residual fidelity items ship with defaults + escalation triggers. The dual-CPU SoftCard board is **ADR 0015**; the CP/M
deliverable (Videx second-display seam + CP/M asset/licensing posture) is **ADR 0016**. Designer: the surfaces are one
`IDisplayDevice` (Apple video, multi-mode), one `IKeyboardSink` (uppercase-only ][+ keymap), one `IAudioSink` (1-bit
speaker) — plus a disk-image selection affordance (which `.dsk`/`.woz` to insert). Planner can decompose §2 into PRs per
the sibling report's PR decomposition.*
