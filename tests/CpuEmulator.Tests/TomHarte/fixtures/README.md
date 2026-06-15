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
