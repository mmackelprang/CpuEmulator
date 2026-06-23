// The STANDING performance profiler for ADR 0022 (the performance feedback loop) — item A.
//
// Successor to the throwaway bench/hotop-profiler/. A developer/agent-invoked dev tool, NOT in any
// shipping/runtime/test graph. It drives BOTH the bench W-kernels (always available) AND the real
// machine boots (DOS 3.3, CP/M 2.2, apl2cpm3 CP/M 3.1, Apple Pascal, Spectrum) through the SAME
// factories the live machines use — skip-with-note when an asset is absent — captures both tiers where
// applicable (the hot-op histogram + the IJitMetrics counters incl. the new chain/dispatch + block-cache
// hit/miss counters, recompile/eviction churn, real-time ratio, GC allocs), and emits a versioned,
// diffable profile.json per system x workload under bench/results/profiles/<system>/<workload>.json plus a
// generated bench/results/profiles/INDEX.md.
//
// Run:  dotnet run -c Release --project bench/profiler
//
// Frozen budgets (committed here, like the W-kernel caps — ADR 0022 §6.4 / OQ2). Tuned so each run is
// stable but completes well under a minute. The chosen unit (cycles vs instructions) is recorded in each
// profile's budgetUnit field.

using System.Diagnostics;
using CpuEmulator.Benchmarks;
using CpuEmulator.Core;
using CpuEmulator.Machines;
using CpuEmulator.Peripherals;
using CpuEmulator.Profiler;

// ── frozen budgets ───────────────────────────────────────────────────────────────────────────────────
const long KernelInstrBudget = 20_000_000;   // instructions per kernel hot-op/counter window (== old InstrCap)
const long Dos33Cycles = 5_000_000;           // Apple ][+ boots to a BASIC prompt in ~500K; a 5M window is steady
const long SpectrumCycles = 20_000_000;       // 48K ROM boots to copyright fast; a representative steady window
const long SoftCardCpmCycles = 8_000_000;     // CP/M 2.2 SoftCard: a few M cycles of the dual-CPU boot
const long Apl2Cpm3Cycles = 60_000_000;       // CP/M 3.1 boot is long; a fixed representative window (not run-to-A>)
const long PascalCycles = 30_000_000;         // boots to COMMAND: ~75M; a shorter representative steady window

string commit = GitShort();
HostInfo host = new(
    Cpu: Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    Os: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Dotnet: Environment.Version.ToString());

string repoRoot = FindRepoRoot();
string profilesDir = Path.Combine(repoRoot, "bench", "results", "profiles");
Directory.CreateDirectory(profilesDir);

var emitted = new List<SystemProfile>();
void Emit(SystemProfile p)
{
    string dir = Path.Combine(profilesDir, p.System);
    Directory.CreateDirectory(dir);
    string path = Path.Combine(dir, p.Workload + ".json");
    File.WriteAllText(path, ProfileJson.Serialize(p));
    emitted.Add(p);
    Console.WriteLine($"  wrote {Path.GetRelativePath(repoRoot, path)}");
}

void Skip(string system, string workload, string reason)
    => Console.WriteLine($"  SKIP {system}/{workload}: {reason}");

Console.WriteLine("# ADR 0022 standing profiler");
Console.WriteLine($"# commit {commit}  host {host.Os}  dotnet {host.Dotnet}");
Console.WriteLine();

// ── the bench W-kernels (always available) ───────────────────────────────────────────────────────────
Console.WriteLine("## bench kernels");
KernelProfiler.RunAll(commit, host, Emit, Skip, KernelInstrBudget);
Console.WriteLine();

// ── the real boots (asset-gated, skip-with-note) ─────────────────────────────────────────────────────
Console.WriteLine("## real boots");
var boots = new RealBootProfiler(commit, host, Emit, Skip);
boots.ProfileDos33(Dos33Cycles);
boots.ProfileSpectrum(SpectrumCycles);
boots.ProfileSoftCardCpm(SoftCardCpmCycles);
boots.ProfileApl2Cpm3(Apl2Cpm3Cycles);
boots.ProfilePascal(PascalCycles);
Console.WriteLine();

// ── the human-readable backlog surface (INDEX.md) ────────────────────────────────────────────────────
string indexPath = Path.Combine(profilesDir, "INDEX.md");
File.WriteAllText(indexPath, IndexWriter.Render(emitted, commit, host));
Console.WriteLine($"wrote {Path.GetRelativePath(repoRoot, indexPath)}");
Console.WriteLine($"\n[profiler] emitted {emitted.Count} profile(s).");

// ── helpers ──────────────────────────────────────────────────────────────────────────────────────────
static string GitShort()
{
    try
    {
        var psi = new ProcessStartInfo("git", "rev-parse --short HEAD")
        { RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi);
        if (p is null) return "unknown";
        string s = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit(5000);
        return string.IsNullOrEmpty(s) ? "unknown" : s;
    }
    catch { return "unknown"; }
}

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "CpuEmulator.slnx")))
        dir = dir.Parent;
    return dir?.FullName
        ?? throw new InvalidOperationException("could not locate the repo root (CpuEmulator.slnx)");
}
