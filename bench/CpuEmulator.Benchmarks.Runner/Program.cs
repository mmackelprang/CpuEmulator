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

var tierRows = new List<BenchHarness.Row>();
var adapterRows = new List<BenchHarness.Row>();

foreach (var w in workloads)
{
    Console.WriteLine($"\n== {w.Name} ==");

    var t0 = BenchHarness.MeasureTier("our Tier-0 interpreter", Tier0.Run, w);
    Console.WriteLine($"  Tier-0 interpreter : {Describe(t0)}");
    tierRows.Add(new BenchHarness.Row("our Tier-0 interpreter", w.Name, t0));

    var t1 = BenchHarness.MeasureTier("our Tier-1 JIT (chaining on)", Tier1.Run, w);
    Console.WriteLine($"  Tier-1 JIT         : {Describe(t1)}");
    tierRows.Add(new BenchHarness.Row("our Tier-1 JIT (chaining on)", w.Name, t1));

    if (all)
    {
        foreach (var row in BenchHarness.MeasureAdapters(BenchHarness.DefaultAdapters(), w))
        {
            Console.WriteLine($"  {row.Subject,-26}: {Describe(row.Result)}");
            adapterRows.Add(row);
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
        ? $"{r.CyclesPerSecond:N0} cycles/sec ({r.WallSeconds:F3}s) — {r.Note}"
        : $"not run — {r.Note}";
