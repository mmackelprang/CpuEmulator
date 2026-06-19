using BenchmarkDotNet.Running;
using CpuEmulator.Benchmarks;
using CpuEmulator.Benchmarks.Runner;

// The comparative-benchmark runner entry point (spec §9 item 9). Modes:
//   (default) / --report   measure our two tiers (always) + write bench/results/REPORT.md
//   --all                  additionally probe + measure the third-party adapters (skip-with-note)
//   --bdn                  run the statistically-rigorous BenchmarkDotNet harness (TierBenchmarks)
//   --gates                run the Task 9 revisit-gate micro-benches (switch-vs-delegate*, layout)
//
// Our two tiers ALWAYS run (in-process C#); third-party subjects are opt-in (--all) and degrade
// gracefully. Only MEASURED data is written to the report — absent subjects are "not run" rows.
var flags = new HashSet<string>(args, StringComparer.OrdinalIgnoreCase);

if (flags.Contains("--bdn"))
{
    BenchmarkRunner.Run<TierBenchmarks>();
    return;
}

if (flags.Contains("--gates"))
{
    RevisitGates.RunAndPrint();
    return;
}

// Default + --report: the headline comparative run.
bool all = flags.Contains("--all");
Console.WriteLine($"Comparative benchmark run (third-party adapters: {(all ? "enabled --all" : "disabled; pass --all")})");

var workloads = new List<BenchWorkload>();
var w1 = Workloads.KlausOrNull();
if (w1 is not null) workloads.Add(w1);
else Console.WriteLine("W1 (Klaus) skipped — image not in the vector cache (run tools/get-klaus).");
workloads.Add(Workloads.ArithmeticKernel());
workloads.Add(Workloads.SieveKernel());   // W3 — the Sieve compute kernel (Dhrystone-class); always runs

// The Z80 workloads: Z80-W1 (ZEXDOC prefix) only when its binary is present; Z80-W2 (kernel) always.
var z80w1 = Z80Workloads.Z80ZexPrefixOrNull();
if (z80w1 is not null) workloads.Add(z80w1);
else Console.WriteLine("Z80-W1 (ZEXDOC prefix) skipped — zexdoc.com not in the vector cache (run tools/get-zexall).");
workloads.Add(Z80Workloads.Z80ArithmeticKernel());
workloads.Add(Z80Workloads.Z80SieveKernel());   // Z80-W3 — the Sieve compute kernel; always runs

// The 68000 workloads (Milestone B): both are dependency-free hand-written kernels (Option A — no
// external exerciser), so both ALWAYS run. m68k-W1 is the deterministic mixed stream; m68k-W2 is the
// tight ALU/branch kernel. The per-workload loop below measures Tier-0 + Tier-1 for each via the driver
// registry (R1) — so the OUR-tier 68000 rows appear automatically. The 68000 baseline LEADS with
// instructions/sec (its cycle axis is partial on `main`, M4.5d-2 gating — the ReportWriter caveat, B4).
workloads.Add(M68000Workloads.MixedKernel());
workloads.Add(M68000Workloads.ArithmeticKernel());
workloads.Add(M68000Workloads.SieveKernel());   // m68k-W3 — the Sieve compute kernel; always runs

// The 8086 workloads (M6 PR-A): all three are dependency-free hand-written little-endian kernels, so
// all ALWAYS run. 8086-W2 is the tight ALU/branch kernel; 8086-W1 is the mixed-instruction stream;
// 8086-W3 is the compute kernel. The per-workload loop below measures Tier-0 + Tier-1 for each via the
// driver registry — so the OUR-tier 8086 rows appear automatically. The 8086 is all-fallback on `main`,
// so Tier-1 == Tier-0 (the honest "before" the later 8086 hot-op emit subtracts from), and the baseline
// LEADS with instructions/sec (its cycle axis is rudimentary — the ReportWriter caveat).
workloads.Add(M8086Workloads.ArithmeticKernel());   // 8086-W2 — the hot ALU/branch kernel
workloads.Add(M8086Workloads.MixedKernel());        // 8086-W1 — the mixed-instruction stream
workloads.Add(M8086Workloads.SieveKernel());        // 8086-W3 — the compute kernel

var tierRows = new List<BenchHarness.Row>();
var adapterRows = new List<BenchHarness.Row>();

foreach (var w in workloads)
{
    Console.WriteLine($"\n== {w.Name} ==");

    // MeasureTierCounted captures BOTH cycles AND the guest instruction count (Task B2): the 68000
    // rows carry instructions/sec (its cycle-axis-independent headline); the 6502/Z80 rows leave it 0.
    // The cap-aware overload (Task 1) bounds each measurement to BenchHarness.PerMeasurementWallCap so a
    // pathological run (the 6502 W1 Klaus JIT thrashes the recompiler) is time-bounded + flagged, never a
    // stall; a fast workload never reaches the deadline (byte-for-byte unchanged). Explicit lambdas pick
    // the cap-aware Tier{0,1}.RunCounted(w, cap) overload unambiguously.
    var t0 = BenchHarness.MeasureTierCounted("our Tier-0 interpreter",
        (BenchWorkload bw, TimeSpan? cap) => Tier0.RunCounted(bw, cap), w, BenchHarness.PerMeasurementWallCap);
    Console.WriteLine($"  Tier-0 interpreter : {Describe(t0)}");
    tierRows.Add(new BenchHarness.Row("our Tier-0 interpreter", w.Name, t0, w.Architecture));

    var t1 = BenchHarness.MeasureTierCounted("our Tier-1 JIT (chaining on)",
        (BenchWorkload bw, TimeSpan? cap) => Tier1.RunCounted(bw, cap), w, BenchHarness.PerMeasurementWallCap);
    Console.WriteLine($"  Tier-1 JIT         : {Describe(t1)}");
    tierRows.Add(new BenchHarness.Row("our Tier-1 JIT (chaining on)", w.Name, t1, w.Architecture));

    if (all)
    {
        foreach (var row in BenchHarness.MeasureAdapters(BenchHarness.AdaptersFor(w.Architecture), w))
        {
            Console.WriteLine($"  {row.Subject,-26}: {Describe(row.Result)}");
            adapterRows.Add(row);   // MeasureAdapters now sets Architecture from the workload
        }
    }
}

if (flags.Contains("--report") || flags.Count == 0 || all)
{
    string md = ReportWriter.Render(tierRows, adapterRows, klausAvailable: w1 is not null);
    string path = ReportWriter.WriteDefault(md);
    Console.WriteLine($"\nReport written: {path}");

    // The headline comparison (M6): the machine-readable twin of the REPORT.md comparison section.
    // The cited registry reserves the 68000 "best existing" column as a [cited] placeholder until the
    // head-to-head Musashi number lands (plan Task M4).
    var cited = ReferenceNumbers.Load();
    var model = ComparisonTableWriter.Build(tierRows, adapterRows, cited);
    string cmpPath = ComparisonTableWriter.WriteComparisonJsonDefault(ComparisonTableWriter.RenderJson(model));
    Console.WriteLine($"Comparison JSON written: {cmpPath}");
}

static string Describe(AdapterResult r)
{
    if (!r.Ran) return $"not run — {r.Note}";
    // A capped run (Task 1) stopped at the wall deadline: flag it distinctly so the console reader knows
    // the rate is over a bounded window (same rate, bounded time — SMC-pathological), not the full budget.
    string capped = r.Capped ? $" [CAPPED at {BenchHarness.PerMeasurementWallCap.TotalSeconds:0.#}s — SMC-pathological]" : "";
    return r.InstructionsPerSecond > 0
        ? $"{r.InstructionsPerSecond / 1_000_000.0:N1} guest-MIPS ({r.CyclesPerSecond:N0} cycles/sec, {r.WallSeconds:F3}s){capped} — {r.Note}"
        : $"{r.CyclesPerSecond:N0} cycles/sec ({r.WallSeconds:F3}s){capped} — {r.Note}";
}
