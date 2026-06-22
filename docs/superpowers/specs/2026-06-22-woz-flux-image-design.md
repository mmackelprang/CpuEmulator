# Design — `WozFluxImage` (the `.woz`-file byte parser)

**Date:** 2026-06-22
**Queue row:** W
**Roadmap:** Apple ][+ backlog row W (`docs/ROADMAP.md` — "Remaining in the Apple ][+ space")
**Status:** Spec (autonomous scoping per owner authorization)
**Relation to prior work:** *extends* PR-F (`docs/superpowers/plans/2026-06-20-apple2-pr-f-disk-ii-woz.md`),
which shipped the `.woz`/LSS nibble **read path** + the `IFluxImage` track-bitstream **seam** but **not** the
`.woz`-**file** byte parser. This spec closes that half — the "full `.woz` fidelity upfront" decision.

---

## 1. Problem

The Apple ][+ Disk II controller (`Apple2DiskII`) reads a track bitstream through the `IFluxImage` seam
(`src/CpuEmulator.Core/IFluxImage.cs`). Two implementations ship today: `SyntheticFluxImage` (test double)
and `DskFluxImage` (re-nibblizes a `.dsk`/`.po` logical-sector image onto the seam). There is **no parser
for the native `.woz` file format**, so:

- `DiskImageFactory.FromBytes(bytes, DiskFormat.Woz)` throws `NotSupportedException`
  (`src/CpuEmulator.Surface.Web/DiskImageFactory.cs:32-33`).
- `DiskCatalog` lists every `.woz` library entry as `Supported: false`
  (`src/CpuEmulator.Machines/DiskCatalog.cs:46`).
- `UploadValidator` validates the `.woz` magic, then honestly rejects with
  "`.woz` upload isn't supported yet" (`src/CpuEmulator.Surface.Web/UploadValidator.cs`).

`.woz` is the **flux-faithful** Apple disk format (the WOZ project's canonical preservation format). Copy-
protected disks (most commercial Apple software) cannot be expressed as `.dsk`/`.po` logical sectors — they
*only* exist as `.woz` flux. Until `WozFluxImage` ships, the surface's disk-UX (insert/upload/library) is
limited to unprotected DOS 3.3 / ProDOS images.

## 2. Goal & non-goals

**Goal:** a thin `WozFluxImage : IFluxImage` that parses a real `.woz` file (WOZ2 / WOZ2.1 — `WOZ2` magic)
into per-track bitstreams the **unchanged** `Apple2DiskII` head reads identically to `DskFluxImage`/
`SyntheticFluxImage`, wired through `DiskImageFactory` so `.woz` insert/upload/library work end-to-end.

**Non-goals (scoped out — recorded so Builder does not over-build):**

- **WOZ1 (`WOZ1` magic).** WOZ1 is the legacy format (fixed 6656-byte track slots, no `BIT_COUNT` field,
  no FLUX chunk). The owner-fetched test corpus and essentially all modern `.woz` images are WOZ2. We parse
  **WOZ2 only**; a WOZ1 file is rejected with a clear `InvalidDataException` ("WOZ1 not supported; re-image
  as WOZ2"). *Decision W-1.* (WOZ1 is a thin future follow-on if a real WOZ1 asset ever appears; YAGNI now.)
- **The `FLUX` chunk (WOZ2.1 raw-flux tracks).** WOZ2.1 optionally adds a `FLUX` chunk for tracks captured
  as raw flux-transition timing rather than decoded bits. Our `IFluxImage` seam is a **bit** array, not a
  flux-timing array; consuming `FLUX` would require a flux-to-bits resolver the controller does not have.
  We parse the `TRKS` **bitstream** tracks (the universal path — every WOZ2 image has them; WOZ2.1's `FLUX`
  is an *optional addition*, and the canonical `TMAP`→`TRKS` bit tracks still cover the disk). If a track's
  `TMAP` entry points only at a FLUX-block track with no bitstream, that track reads as empty (the head sees
  no data — same as a blank drive). *Decision W-2.*
- **Writing / `.woz` save-back.** `IsWriteProtected` is honored (reads only). The controller already ignores
  writes to a write-protected image; `.woz` write-back (re-emitting the container) is out of scope. *W-3.*
- **Quarter-track / half-track fidelity beyond what `TMAP` provides.** We honor the `TMAP` 160-entry
  quarter-track map exactly as WOZ2 defines it (see §4); we do **not** invent interpolation. *W-4.*

## 3. Where it lives

| Artifact | Path | Why |
|---|---|---|
| `WozFluxImage` (the parser) | `src/CpuEmulator.Peripherals/WozFluxImage.cs` | Beside `DskFluxImage`/`SyntheticFluxImage` (same assembly, same seam). |
| `Crc32` helper (internal) | `src/CpuEmulator.Peripherals/Woz/Crc32.cs` (or a private static in `WozFluxImage`) | WOZ stores a CRC32 over the post-CRC bytes; we verify it. The repo's only CRC32 is in `tools/BootProbe` (a dev tool, not shippable `src/`), so we add a small internal one. The algorithm is the standard zlib/PNG CRC32 (poly `0xEDB88320`, init/final XOR `0xFFFFFFFF`) — identical to BootProbe's, but `src`-resident. *Decision W-5.* |
| Asset loader | `src/CpuEmulator.Machines/WozAsset.cs` | Mirrors `SpectrumRom`/`Apl2Cpm3` — `TryGetPath`/`Load` over `<cache>/woz/<name>.woz`. |
| Fetch script | `tools/get-woz-disks.{sh,ps1}` | Fetch-on-demand, **never vendored** (a real public-domain `.woz`). |
| Factory wiring | edit `src/CpuEmulator.Surface.Web/DiskImageFactory.cs` | Replace the `.woz` `NotSupportedException` with `new WozFluxImage(bytes)`. |
| Catalog wiring | edit `src/CpuEmulator.Machines/DiskCatalog.cs:46` | `Supported: true` for `.woz` (it now parses). |
| Upload wiring | edit `src/CpuEmulator.Surface.Web/UploadValidator.cs` | After the magic check, accept `.woz` (the not-yet-supported reject is removed; a malformed body throws on parse, surfaced as the existing upload error). |

`WozFluxImage` is a **pure peripheral** with no dependency on `Surface.Web` or `Machines`; the wiring edits
are one-liners in those assemblies (the dependency direction stays correct — `Surface.Web`/`Machines`
already reference `Peripherals`).

## 4. The WOZ2 container (what we parse)

A WOZ2 file is: an 8-byte header (`57 4F 5A 32 FF 0A 0D 0A` = `"WOZ2"` + `$FF 0A 0D 0A`), a 4-byte CRC32
(little-endian, over **all bytes after the CRC field**; `0` means "do not verify"), then a sequence of
chunks. Each chunk is `[4-byte ASCII id][4-byte LE size][size bytes]`. We read these chunks:

- **`INFO` (60 bytes):** version, disk type (1 = 5.25"), write-protected flag (offset 5), `boot_sector_format`,
  `optimal_bit_timing`, etc. We use: **write-protected** (→ `IsWriteProtected`) and **disk type** (assert
  5.25" — the only Disk II type; a 3.5" image is rejected with `InvalidDataException`). *Decision W-6.*
- **`TMAP` (160 bytes):** the quarter-track → TRKS-track map. 160 entries (one per quarter-track, tracks
  0…39.75), each a `byte` index into the `TRKS` track table, or `$FF` = "no track here" (empty). The Disk II
  head position is a half-track (`_halfTrack`), and `Apple2DiskII` maps `track = _halfTrack / 2`
  (`Apple2DiskII.cs`). WOZ's quarter-track index for a whole track `t` is `t * 4`. So
  `TrackBits(t)` resolves `TMAP[t * 4]` → a `TRKS` index (or empty). *Decision W-7.*
- **`TRKS`:** 160 × 8-byte `TRK` entries (`starting_block` u16 LE, `block_count` u16 LE, `bit_count` u32 LE),
  followed by the bitstream blocks. A track's bits live at byte offset `starting_block * 512`, span
  `block_count * 512` bytes, and the **valid bit count is `bit_count`** — which maps **directly** onto
  `IFluxImage.TrackBitLength`. The bytes are already MSB-first packed (WOZ spec), exactly what
  `IFluxImage.TrackBits` requires. *This is the load-bearing impedance match — see §5.*
- **`META` / `FLUX` / `WRIT`:** skipped (META is human metadata; FLUX/WRIT are out of scope per W-2/W-3).

**CRC32 verification (the un-fakeable real-bytes gate):** after parsing, we verify the header CRC32 over the
post-CRC bytes. A non-zero CRC that mismatches → `InvalidDataException` ("WOZ CRC32 mismatch"). A zero CRC →
skip (the spec's "do not verify" sentinel).

## 5. Why it is genuinely thin (the seam match)

`IFluxImage` requires, per track: an MSB-first packed byte span (`TrackBits`) and an exact valid bit count
that loops (`TrackBitLength`). WOZ2 `TRKS` stores **exactly that** — MSB-first packed bits + a `bit_count`
loop length. So `WozFluxImage` is, in essence:

- Parse the chunks once in the constructor (validate magic/CRC/disk-type; build the `TMAP` + `TRK` tables;
  keep the raw byte buffer).
- `TrackCount` → 40 (the 5.25" whole-track count; or derived from the highest mapped `TMAP` whole-track).
- `TrackBits(t)` → `TMAP[t*4]` → `TRK.starting_block*512 .. +block_count*512` slice of the buffer
  (or an empty span if `$FF`).
- `TrackBitLength(t)` → that `TRK`'s `bit_count` (or `0` if empty).
- `IsWriteProtected` → the `INFO` flag.

No re-nibblizing, no GCR synthesis (unlike `DskFluxImage`) — the bits are already on-disk-faithful. The
**only** real work is container parsing + validation. This is what makes W "thin."

## 6. Architecture & data flow

```
.woz bytes ─► WozFluxImage(bytes)            (constructor: parse + validate, throw on malformed)
                 │  parse header (magic, CRC32-verify)
                 │  parse INFO  → writeProtected, diskType
                 │  parse TMAP  → quarterTrack[160]
                 │  parse TRKS  → trk[160] {start, blocks, bitCount}; keep raw buffer
                 ▼
            IFluxImage  ──(unchanged seam)──►  Apple2DiskII head
                 TrackBits(t)      = buffer[ trk[TMAP[t*4]].start*512 .. ]
                 TrackBitLength(t) = trk[TMAP[t*4]].bitCount
                 IsWriteProtected  = INFO.writeProtected
```

`DiskImageFactory.FromBytes(bytes, Woz)` → `new WozFluxImage(bytes)`. The surface's existing
insert/upload/library paths (Q/R/S) already call `DiskImageFactory`/the catalog; they need no logic change
beyond flipping the `.woz` `Supported` flag and removing the upload reject.

## 7. Error handling

All malformed input throws a typed `InvalidDataException` (or `ArgumentException` for null/short input) with
a specific message: wrong magic / WOZ1 / missing `INFO`|`TMAP`|`TRKS` / disk-type ≠ 5.25" / CRC32 mismatch /
a `TRK` slice that runs past the buffer. The upload path surfaces these as its existing generic upload error
(the message need not round-trip to the client; the server log carries the specific reason). The constructor
is the single validation choke point — once a `WozFluxImage` exists, every track access is in-bounds.

## 8. Testing — the un-fakeable gates

**Asset-free (always run):**

1. **Container parse on a hand-built minimal WOZ2.** A test builder emits a tiny valid WOZ2 (header + correct
   CRC32 + `INFO` + `TMAP` + `TRKS` with one short track of known bits). Assert `TrackCount`, `TrackBits`
   round-trips the exact bytes, `TrackBitLength == bit_count`, `IsWriteProtected` reads the INFO flag.
2. **CRC32 correctness.** The internal `Crc32` matches a known vector (e.g. CRC32("123456789") = `0xCBF43926`)
   AND the parser **rejects** a WOZ2 whose stored CRC is wrong (flip one data byte → `InvalidDataException`).
   This is the "CRC32 round-trip asserted on real bytes" gate.
3. **Rejections:** WOZ1 magic → reject; 3.5" disk type → reject; truncated `TRKS` slice → reject.
4. **TMAP round-trip:** a quarter-track map with a known whole-track→TRKS mapping resolves `TrackBits(t)` to
   the right TRK; an `$FF` entry yields an empty track (length 0). *(The "TMAP round-trip asserted on real
   bytes" gate.)*

**Asset-gated (`[WozDiskFact]`, skip-with-note when the `.woz` asset is absent) — THE headline un-fakeable
gate:** a **real, fetch-on-demand, never-vendored** public-domain `.woz` image is parsed, its CRC32 verified
on the real bytes, and its track-0 bitstream is read by the **live `Apple2DiskII` head** on the interpreter
tier — the controller (unchanged from PR-F) finds an address-field prologue (`D5 AA 96`) and a data-field
prologue (`D5 AA AD`) in the nibble stream the head shifts out, proving the parsed bits boot/read on the real
Disk II path. (Pattern: `DskFluxImageTests`'s "a real 6502 finds the data field" interpreter gate, but over
real `.woz` bytes instead of synthesized ones.)

**Asset choice:** a small, freely-redistributable `.woz` (e.g. an Apple ][ demo/firmware disk from the WOZ
project's public test images, or a DOS 3.3 master re-imaged to WOZ2). The fetch script documents the source +
a length/magic sanity check; sign-off rides the existing disk-asset sign-off (same as the CP/M masters). If
no suitable public asset is locatable at Builder time, the asset-gated gate stays skip-with-note and the
asset-free gates 1–4 (which fully exercise the parser, incl. CRC32 on real constructed bytes) carry the row —
Builder flags this in the PR. *Decision W-8.*

## 9. Invariants honored

- **Interpreter-as-oracle:** the headline gate runs on the interpreter tier (the Disk II / nibble path is
  interpreter-only; no JIT emit involved). The asset-free gates are tier-agnostic byte assertions.
- **AOT-clean Core:** `WozFluxImage` lives in `Peripherals`, implements the existing `Core` `IFluxImage`;
  no `Core` change. No reflection, no dynamic codegen.
- **Fetch-on-demand assets, never vendored:** the real `.woz` is fetched by `tools/get-woz-disks.{sh,ps1}`
  into `<cache>/woz/`, skip-with-note when absent — identical to every other asset in the tree.
- **No regression to the shipped `.dsk`/`.po`/CP/M paths:** the only edits to shipped files are the three
  one-line wirings (factory dispatch, catalog `Supported`, upload accept); `DskFluxImage`/`Apple2DiskII`
  are byte-for-byte unchanged. The full Apple2 disk suite is the regression guard.

## 10. Dependencies & priority

- **Deps:** F (✅ shipped — the seam + read path). No other deps.
- **Does not block** R/S/T (those are end-to-end-complete for `.dsk`/`.po`); W *upgrades* them for `.woz`.
- **Priority:** first of the three (it completes a user-visible Apple ][+ capability and unblocks the
  surface's `.woz` UX; it is the only one of the three with downstream user impact).

## 11. Scoping decisions (recorded — autonomous per owner authorization)

- **W-1:** WOZ2 only; WOZ1 rejected (legacy, no test corpus, YAGNI).
- **W-2:** `TRKS` bitstream tracks only; `FLUX` chunk skipped (seam is bits, not flux timing).
- **W-3:** read-only; no `.woz` write-back.
- **W-4:** honor `TMAP` quarter-track map verbatim; no interpolation.
- **W-5:** add a small `src`-resident standard CRC32 (BootProbe's is a dev tool).
- **W-6:** assert disk type = 5.25"; reject 3.5".
- **W-7:** whole-track `t` resolves `TMAP[t*4]`.
- **W-8:** if no public `.woz` asset is locatable, the asset-gated gate skips-with-note; asset-free gates
  (incl. real-bytes CRC32) carry the row.

None of these are cross-cutting architecture; all are local format-scoping calls. No Architect escalation
needed (the `IFluxImage` seam is fixed and shipped; W is a pure consumer).
