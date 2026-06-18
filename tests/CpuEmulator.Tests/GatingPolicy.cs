namespace CpuEmulator.Tests;

/// <summary>
/// THE VERIFICATION GATING POLICY (encoded by PR-T3). What runs per-PR vs periodically:
///
/// | Workload                       | Per-PR (routine)                  | Periodic / pre-arc / pre-merge      |
/// |--------------------------------|-----------------------------------|-------------------------------------|
/// | TomHarte interpreter sweeps    | sampled (default 100)             | CPUEMULATOR_UAT=full (full per-file)|
/// | TomHarte JIT sweeps            | sampled, parallel per-partition   | CPUEMULATOR_UAT=full                |
/// | Klaus interpreter pin          | every run (the oracle)            | —                                   |
/// | Klaus through-JIT functional   | gated CPUEMULATOR_KLAUS=full      | run pre-arc / pre-merge             |
/// | ZEX smoke (wiring)             | every run (~1.3 s)                | —                                   |
/// | ZEXDOC                         | triage pre-check (bounded)        | within CPUEMULATOR_ZEX=full         |
/// | ZEXALL (interp + JIT)          | gated CPUEMULATOR_ZEX=full        | the real composition gate           |
/// | Differential fuzzer            | every run (covers JIT each run)   | —                                   |
///
/// Rationale (PR-1 precedent): full ZEXDOC-through-JIT is a periodic / pre-arc gate, not per-PR. The heavy JIT
/// exercisers (Klaus-JIT, ZEXDOC/ZEXALL-JIT) sit behind env gates; per-run JIT coverage is the differential fuzzer
/// + the sampled JIT TomHarte sweeps + the interpreter Klaus pin — all of which run every invocation.
/// </summary>
internal static class GatingPolicy { }
