# Test-suite speedup — before → after (M6 test-infra arc, 2026-06-18)

The verification suite was made substantially faster across the T1–T4 arc (levers 1–7),
with no/low coverage loss — the full gates are preserved and reachable via env vars.
"Routine" = the default per-PR run, with no `*_full` gates set.

## Aggregate — routine verification suite

| | commit | default sample | duration | result |
|---|---|---|---|---|
| **Before** | `750f669` (pre-arc) | 200 | **8m 29s** (514s) | 6612 passed |
| **After**  | `bb60d48` (post-arc) | 100 | **5m 21s** (325s) | 6620 passed, 1 gated-skip |

**~1.59× / −37%** on the routine suite. The routine aggregate is *execution-floored*:
running the cases (interpreter + JIT) dominates the wall-clock and is unchanged by these
levers. The large per-lever wins below dominate the **periodic / full** gates
(`CPUEMULATOR_UAT=full`, full TomHarte/ZEX sweeps) — the historical "most of a day" runs —
where parse-everything + per-case allocation scaled to TB-level transient garbage.

## Per-lever (isolated subset measurements)

| PR | Lever | Win |
|---|---|---|
| T1 (#67) | parse cache + bounded parse + sampling unification (1, 7) | **17.89×** parse-isolated (68000 subset) |
| T2 (#68) | per-worker RAM-arena + AddressSpace page-table pooling (2) | **−28%** wall, **~4–5 orders of magnitude** fewer transient allocations (8088 subset) |
| T3 (#69) | parallelize the 8088+68000 JIT sweeps + gating policy (3, 5, 6) | heavy JIT tier now uses all 16 threads; Klaus & ZEXDOC moved off the per-PR path |
| T4 (#70) | per-worker JIT reuse — flush-and-reuse `JittedCpu` (4) | **~1.67×** (JIT sweep subset); behind a byte-for-byte equivalence drop-gate |

## Gating policy (encoded in `tests/CpuEmulator.Tests/GatingPolicy.cs`)

- **Per-PR (routine):** sampled interpreter sweeps · sampled + parallel JIT sweeps ·
  Klaus interpreter pin · ZEX wiring smoke · ZEXDOC triage pre-check · differential fuzzer.
- **Periodic / pre-merge:** `CPUEMULATOR_UAT=full` (full TomHarte) · Klaus-through-JIT
  (`CPUEMULATOR_KLAUS=full`) · ZEXALL both tiers (`CPUEMULATOR_ZEX=full`).
