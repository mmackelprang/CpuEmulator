# ADR 0021 — Apple II Pascal (UCSD p-System) boots on the Apple ][+: the `.dsk` is DOS-3.3 sector order (no new ordering needed), and the Language Card needs a "read ROM, write RAM" write-through mode

> **Status:** ACCEPTED — **live-proven**: the real Apple II Pascal 1.1 (UCSD p-System II.1) distribution boots to
> the `COMMAND:` outer command line on the plain Apple ][+ board.
> **Date:** 2026-06-23
> **Deciders:** Mark (owner). Drafted + implemented autonomously by Claude during the Pascal bring-up.
> **Reads as ground truth:** (1) the owner-supplied Apple Pascal distribution `.dsk` images (`D:/prj/ROMs/Apple
> Pascal {0..3} - 680-000{3..6}-01.dsk`) — the disk is the oracle; (2) **dmolony/AppleFileSystem** (the Pascal
> sector-interleave table in `ByteCopier.java`) — the independent cross-check of the ordering; (3) Sather,
> *Understanding the Apple II/IIe*, ch. 5 (the Language Card soft-switch truth table) + the canonical Beneath
> Apple DOS / ProDOS interleave tables; (4) a live instruction-step trace of the boot on the real disks; (5) ADR
> 0014 Decision 4 (PR-E, the Language Card) + ADR 0018-C (the LC two-latch write-enable flip-flop, untouched).

---

## 1. Context

The "boot real Apple Pascal" bring-up targeted the original 1979/1980 Apple II Pascal distribution. The prime
suspect, by analogy to the CP/M arc's per-track skew, was the **sector order**: Apple Pascal `.dsk` images were
expected to be in ProDOS/Pascal sector order, requiring a new `SectorOrderKind`.

Two findings emerged, neither matching the prime-suspect hypothesis.

## 2. Finding 1 — the `.dsk` is DOS-3.3 sector order containing a Pascal filesystem; `SectorOrderKind.Dos33` is correct

The Apple Pascal `.dsk` images follow the universal `.dsk` convention: **DOS-3.3 logical sector order on disk**,
regardless of the filesystem they contain. The directory (Pascal block 2, volume name `APPLE0`/`APPLE1`) sits at
file LBA 11 of track 0 — which only resolves to the correct Pascal logical block under the DOS-3.3 interleave.

`DskFluxImage` lays synthesized physical sector `p` from the file LBA `physToLog[p]`, and the unchanged Disk II
BIOS de-skews physical → logical via the ProDOS/Pascal physical interleave. The DOS-3.3-order file routed through
that ProDOS de-skew composes to the correct Pascal logical layout. So the **existing `SectorOrderKind.Dos33`** is
the right ordering — **no new `SectorOrderKind` was added** (additive-but-unnecessary avoided).

**Independent cross-check (dmolony/AppleFileSystem).** That library reads a Pascal filesystem out of a DOS-ordered
`.dsk` with the interleave `{0,14,13,12,11,10,9,8,7,6,5,4,3,2,1,15}` (logical sector → file offset). Composing it
through the ProDOS physical mapping our Disk II head applies yields **exactly the DOS-3.3 table**
`{0,7,14,6,13,5,12,4,11,3,10,2,9,1,8,15}` — the same table empirically derived from the APPLE0/APPLE1 directory.
ProDOS ordering, by contrast, executes garbage and faults on an undefined opcode. **Decision:** `Pascal.Order =
SectorOrderKind.Dos33`.

**Boot topology.** APPLE1 is the **boot** volume (it carries `SYSTEM.APPLE`, the p-machine interpreter, AND
`SYSTEM.PASCAL`); APPLE0 is the **program/compiler** volume (`SYSTEM.COMPILER`/`EDITOR`/`FILER`/`LIBRARY`). The
authentic two-drive distribution boots APPLE1 in drive 1 and APPLE0 in drive 2. (Booting APPLE0 alone reaches the
authentic `NO FILE SYSTEM.APPLE` halt — the boot loader works; the interpreter just is not on that volume.)

## 3. Finding 2 — the Language Card needs a "read ROM, write RAM" write-through mode

With the ordering correct, the boot still faulted via `JMP ($0000)`. A live trace pinned it: the `SYSTEM.APPLE`
loader uses the Language Card's **"read ROM, write RAM"** mode — it write-enables LC RAM while *executing from the
Monitor/Applesoft ROM* (`$C081` read-twice / `$C089`), copies the p-machine interpreter into the banked
`$D000-$FFFF`, then flips to read-RAM (`$C080`) and `JMP ($FFF8)`s into it.

The shipped LC `ApplyMapping` had only three branches (read-RAM/write-RAM, read-ROM read-only, read-RAM
read-only) because the **single-backing page table** (`AddressSpace.PageEntry` has one `Backing` array for both
reads and writes) cannot express **read source ≠ write target**. In the "read ROM, write RAM" mode the LC mapped
the ROM read-only, so the loader's `STA ($3E),Y` interpreter copy hit the write-protected ROM backing and was
silently dropped. LC RAM stayed zero; `$FFF8` read `$00`; `JMP ($0000)` faulted. (The LC's own code comment had
flagged this as out of scope — Apple Pascal is the software that needs it.)

### Decision

In the **read-ROM + write-enabled** mode only, the Language Card takes over `$D000-$FFFF` as an **MMIO
write-through device** — `RemapPeripheral($D000, $3000, this)`, the same `IAddressSpace` seam the Videx `$C800`
window uses. The LC's `Read` returns the system-ROM byte (the read source); `Write` lands in the write-enabled LC
RAM (the bank-selected `$D000` array + the shared `$E000` array); `TryPeek` is the side-effect-free ROM read. When
the loader flips to read-RAM, `ApplyMapping` `Remap`s the now-populated RAM back as a fast-path readable/executable
backing. The three pre-existing modes are unchanged (still fast-path memory `Remap`s).

### Why this is SAFE + bounded (not an Architect-class core change)

- **Entirely on the existing LC `IPeripheral` seam.** No `PageEntry` / `AddressSpace` / page-table change; the LC
  is already a peripheral and already owns the `$D000-$FFFF` remap. The fix reuses the shipped `RemapPeripheral`
  primitive — no new core primitive.
- **JIT coherence holds.** Both `Remap` and `RemapPeripheral` fire `FireRemap` (the JIT invalidation listener), so
  a code page that changes source/target is re-classified + evicted. While `$D000-$FFFF` is MMIO it has no fastmem
  backing, so no compiled block can hold a stale fast-path reference; flipping back to RAM evicts any MMIO-phase
  block. (The boot runs on the interpreter tier; the JIT path is exercised by the existing
  `A_real_program_runs_code_out_of_LC_RAM(Jit)` test.)
- **The write-enable / two-latch flip-flop logic (`Access`, ADR 0018-C) is untouched** — only `ApplyMapping` gained
  one `else if (_writeEnabled)` branch + the `Read`/`Write`/`TryPeek` handlers. The SoftCard 2.2 + apl2cpm3 CP/M-3
  boots + all Language Card unit tests stay green live.

## 4. Gates

- **`PascalBootTests`** (asset-gated, skip-with-note): boots APPLE1 (drive 1) + APPLE0 (drive 2) on the plain
  ][+ board, decodes the live 40-col text page, and asserts the genuine sign-on (`APPLE II PASCAL`, `UCSD PASCAL`)
  + the outer `COMMAND:` line (`E(DIT`/`R(UN`/`F(ILE`/`C(OMP`). The disk is the oracle — the asserted strings are
  the disk's own boot banner, un-fakeable by a dead/garbage board (all-zero RAM decodes as `@`).
- **Two asset-free Language Card unit tests** (the red→green proof of the write-through fix): `$C081` reads ROM
  while writing LC RAM, then `$C083` reads back the written `$D000`/`$E000`/`$FFF8` bytes; and read-ROM
  write-protected drops the writes.
- The human-visible screenshot is `tools/BootProbe --apple-pascal` → `/d/prj/pascal-boot-LIVE.png`.

## 5. Consequences

- Apple Pascal (UCSD p-System) is the third real OS the Apple ][+ boots (DOS 3.3, CP/M, now Pascal).
- The LC "read ROM, write RAM" mode is now modeled faithfully, which any future LC-RAM-loading software (DOS RAM
  cards, integer/floating-point BASIC swap, other p-System builds) also benefits from.
- The Apple Pascal `.dsk` images are **owner-supplied, staged on demand** by `tools/get-apple-pascal.{sh,ps1}`
  from the owner's local source — never vendored (Apple's copyright), the same posture as the CP/M `.dsk`.
