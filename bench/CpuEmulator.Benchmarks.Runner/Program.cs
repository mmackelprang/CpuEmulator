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

// The Z80 workloads: Z80-W1 (ZEXDOC prefix) only when its binary is present; Z80-W2 (kernel) always.
var z80w1 = Z80Workloads.Z80ZexPrefixOrNull();
if (z80w1 is not null) workloads.Add(z80w1);
else Console.WriteLine("Z80-W1 (ZEXDOC prefix) skipped — zexdoc.com not in the vector cache (run tools/get-zexall).");
workloads.Add(Z80Workloads.Z80ArithmeticKernel());

// The 68000 workloads (Milestone B): both are dependency-free hand-written kernels (Option A — no
// external exerciser), so both ALWAYS run. m68k-W1 is the deterministic mixed stream; m68k-W2 is the
// tight ALU/branch kernel. The per-workload loop below measures Tier-0 + Tier-1 for each via the driver
// registry (R1) — so the OUR-tier 68000 rows appear automatically. The 68000 baseline LEADS with
// instructions/sec (its cycle axis is partial on `main`, M4.5d-2 gating — the ReportWriter caveat, B4).
workloads.Add(M68000Workloads.MixedKernel());
workloads.Add(M68000Workloads.ArithmeticKernel());

var tierRows = new List<BenchHarness.Row>();
var adapterRows = new List<BenchHarness.Row>();

foreach (var w in workloads)
{
    Console.WriteLine($"\n== {w.Name} ==");

    // MeasureTierCounted captures BOTH cycles AND the guest instruction count (Task B2): the 68000
    // rows carry instructions/sec (its cycle-axis-independent headline); the 6502/Z80 rows leave it 0.
    var t0 = BenchHarness.MeasureTierCounted("our Tier-0 interpreter", Tier0.RunCounted, w);
    Console.WriteLine($"  Tier-0 interpreter : {Describe(t0)}");
    tierRows.Add(new BenchHarness.Row("our Tier-0 interpreter", w.Name, t0, w.Architecture));

    var t1 = BenchHarness.MeasureTierCounted("our Tier-1 JIT (chaining on)", Tier1.RunCounted, w);
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
}

static string Describe(AdapterResult r) =>
    r.Ran
        ? (r.InstructionsPerSecond > 0
            ? $"{r.InstructionsPerSecond / 1_000_000.0:N1} guest-MIPS ({r.CyclesPerSecond:N0} cycles/sec, {r.WallSeconds:F3}s) — {r.Note}"
            : $"{r.CyclesPerSecond:N0} cycles/sec ({r.WallSeconds:F3}s) — {r.Note}")
        : $"not run — {r.Note}";
