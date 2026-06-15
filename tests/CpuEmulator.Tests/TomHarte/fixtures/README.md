# 680x0 TomHarte loader fixtures

`m68000-sample.json.gz` — a hand-built 2-case fixture in the SingleStepTests/680x0 schema (separate
`usp`/`ssp`, 16-bit `sr`, the 2-word `prefetch` queue [initial + final], `ram` as `[addr, value]` pairs, a
top-level `length` [total cycles], and the `transactions` array as either `["n", cycles]` idle slots or
`[dir, cycles, fc, addr, sizeTag, value]` bus accesses with `.b`/`.w` size tags). It exercises the gzip +
mnemonic-keyed loader path WITHOUT requiring the multi-GB upstream vector download. The state transitions
are illustrative, NOT cycle-accurate — M4.4b asserts the PARSE only; execution-green is M4.5.

## Schema notes (pinned at M4.4b Task 1 against the live SingleStepTests/680x0 repo)

- In-repo path: `68000/v1/*.json.gz` (NOT a top-level `v1/`); fetched by `tools/get-test-vectors-68000.ps1`.
- Transaction field 2 = the per-slot CYCLE COUNT (the case `length` == the sum of field 2 across its
  transactions) — confirmed, not the ADR-0004-flagged unknown.
- The 68000 bus is 16-bit, so bus transactions are `.b`/`.w` only (a `.l` access decomposes into two `.w`
  transactions — there is no `.l` at the bus level).
- Direction `"n"` is an idle slot (no bus access); `"r"`/`"w"` are bus reads/writes.

## Regenerate

The committed file MUST be real gzip bytes (so the loader's `GZipStream` path is exercised). From this
directory, write the source JSON below to `m68000-sample.json`, then:

    gzip -c m68000-sample.json > m68000-sample.json.gz

The source JSON (also recorded in the plan `docs/superpowers/plans/2026-06-15-m4-4b-…md` Task 4 Step 1):

```json
[
  { "name": "ADD.w fixture",
    "initial": { "d0":1,"d1":2,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                 "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                 "usp":16384,"ssp":32768,"sr":8192,"pc":1024,
                 "prefetch":[53328,0],"ram":[[1024,208],[1025,80]] },
    "final":   { "d0":3,"d1":2,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                 "a0":4096,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                 "usp":16384,"ssp":32768,"sr":8192,"pc":1028,
                 "prefetch":[0,1],"ram":[[1024,208],[1025,80]] },
    "length": 8,
    "transactions":[["n",2],["r",4,6,1024,".w",53328],["r",2,6,1026,".w",1]] },
  { "name": "CLR.b fixture",
    "initial": { "d0":255,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                 "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                 "usp":0,"ssp":1024,"sr":8192,"pc":2048,
                 "prefetch":[16896,0],"ram":[[2048,66],[2049,0]] },
    "final":   { "d0":0,"d1":0,"d2":0,"d3":0,"d4":0,"d5":0,"d6":0,"d7":0,
                 "a0":0,"a1":0,"a2":0,"a3":0,"a4":0,"a5":0,"a6":0,
                 "usp":0,"ssp":1024,"sr":8196,"pc":2050,
                 "prefetch":[0,2],"ram":[[2048,66],[2049,0]] },
    "length": 4,
    "transactions":[["r",4,6,2048,".b",66]] }
]
```

If `gzip` is unavailable, write the same bytes with a one-off C# `GZipStream` write (`CompressionMode
.Compress`); the only requirement is real gzip bytes.

---

# 8088 (M5) TomHarte loader fixture

`m8088-sample.json.gz` — the **first 2 real cases** from the live `SingleStepTests/8088` `v2/00.json.gz`
(opcode `00` = `ADD r/m8, r8`), gzipped (827 bytes). Unlike the hand-built 68000 fixture, these are verbatim
upstream cases — so the fixture is also a ground-truth schema record. The two cases are `add byte
[ss:bp+di-64h], cl` (3 bytes, a memory form with an `ss:` segment, 28 cycles) and `add bh, cl` (2 bytes, a
register form, 8 cycles). A future M5.4 `M8088TomHarteLoader` parse-proof can run against this WITHOUT the
multi-GB upstream download. Pinned at the M5 8088 recon pass (read-only, against the live repo + its README).

## Schema notes (pinned against the live SingleStepTests/8088 v2 repo + README — ADR 0006 Decision 5)

- **File layout:** `v2/*.json.gz` — **GZIP-compressed**, **OPCODE-HEX-keyed** (`00.json.gz`, `88.json.gz`,
  `A4.json.gz`), 324 files, 10,000 cases each (string ops 2,000; trivial families 1,000). Fetched by
  `tools/get-test-vectors-8088.ps1` → cache `<dest>/8088/v2`. **This is the load-bearing divergence from the
  680x0 set: hex-keyed (like 6502/Z80), not mnemonic+size-keyed (like 680x0) — but gzipped (like 680x0).**
- **`initial.regs`** carries the full 14 keys: `ax bx cx dx cs ss ds es sp bp si di ip flags` (all 16-bit; one
  combined `flags`, NOT split). **`final.regs` is SPARSE** — only the registers that CHANGED appear (e.g.
  `add bh,cl` → `final.regs` = `bx`, `ip`, `flags`; `mov dh,dh` → just `ip`). The whole 16-bit `flags` value
  appears if ANY flag changed. **This sparse-final-state is the second divergence from the 680x0 set** (whose
  `final` repeats every register); the M5.4 runner must MERGE `final.regs` over `initial.regs`, not replace.
- **`ram`** = `[addr, value]` pairs (20-bit physical addresses up to `0xFFFFF`); **unsorted** (access order, a
  V2 change); `final.ram` lists only the cells that changed.
- **`queue`** = a byte array (the 8088 BIU prefetch queue, max 4 bytes), present in BOTH `initial` and `final`.
  An EMPTY `initial.queue` ⇒ non-prefetched (fetch normally); a NON-empty one ⇒ start fully prefetched (the
  bytes are pre-installed; add their count to PC). All post-initial fetched bytes are `0x90` (NOP), so the
  `final.queue` is NOPs. **The queue is the timing-axis dimension** (ADR 0006 Decision 6) — the correctness
  axis (`regs`+`ram`) does not need it; it is asserted only in the late M5.5e timing sub-milestone.
- **`cycles`** = a per-CLOCK array; each entry is an **11-element** tuple (NOT the 680x0's 6-tuple
  transactions — far finer-grained): `[pin-bitfield, mux-bus(20-bit addr/data), seg-status, mem-status(RAW/
  ---), io-status(RAW/---), BHE, data-bus, bus-status(CODE/MEMR/MEMW/IOR/IOW/INTA/HALT/PASV), T-state(T1-T4/
  Ti/Tw), queue-op(F/S/E/-), queue-byte]`. Pin-bitfield bit0 = ALE (latch the address on ALE). This is the
  **timing axis** (ADR 0006 Decision 6); the correctness gate ignores it.
- **Top-level fields:** `name` (disassembly), `bytes` (raw instruction bytes), `initial`, `final`, `cycles`,
  `hash` (SHA1), `idx`. A separate `metadata.json` (repo root) lists per-opcode `status`
  (normal/prefix/alias/undocumented/undefined/fpu), a `flags`/`flags-mask` (which flags are left UNDEFINED —
  AND the mask to clear them before comparing), and a `reg` sub-object for opcode-group (ModR/M `reg`-extended)
  opcodes (`80`/`81`/`F6`/`FF`/…). Prefix opcodes per metadata: `26 2E 36 3E` (segment overrides) + `F0 F1`
  (LOCK) + `F2 F3` (REP/REPNE) — exactly ADR 0005 Decision 2 / ADR 0006 Decision 1.
- **Divergences a future M5.4 loader/runner MUST handle** (vs the 680x0 loader): (1) hex-keyed filenames, so
  the resolver is closer to `TomHarteVectors` (6502/Z80) than `M68000TomHarteVectors`; (2) **sparse `final`**
  (merge, don't replace); (3) the `flags-mask` from `metadata.json` must be applied before asserting `flags`
  (undefined flags vary by silicon); (4) string ops (`A4`–`A7`,`AA`–`AF`) may carry a `REP`/`REPE`/`REPNE`
  prefix with `CX` masked to 7 bits; (5) random segment-override prefixes prepended to a % of cases (the
  override may legally have no effect); (6) the `cycles`/`queue` timing axis is per-clock and far richer than
  the 680x0 transaction trace — sequence it LATE (ADR 0006 Decision 6).

## Regenerate (8088)

The committed file is the first 2 real cases of upstream `v2/00.json.gz`. After fetching the vectors
(`tools/get-test-vectors-8088.ps1`), from `<cache>/8088/v2`:

    python -c "import gzip,json; d=json.load(gzip.open('00.json.gz')); \
      json.dump(d[:2], gzip.open('m8088-sample.json.gz','wt',encoding='utf-8'), indent=1)"

