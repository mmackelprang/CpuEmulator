namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>A JS 6502 (@sfotty-pie/sfotty, "cycle-exact 6502 for Node.js") via a node subprocess.
/// Probes for node + the fetched sfotty package + the authored glue script; skips-with-note when
/// absent. The glue resolves sfotty from the bench cache's node_modules (CPUEMULATOR_BENCHCACHE),
/// so node loads it via NODE_PATH.</summary>
public sealed class JsEmulatorAdapter : IEmulatorAdapter
{
    private const long JsW2MeasureCycles = 20_000_000;   // V8 is fast; a larger slice is fine

    public string Name => "sfotty (JavaScript/Node)";

    public bool Probe(out string reason)
    {
        string glue = Path.Combine(BenchPaths.Glue, "sfotty_runner.mjs");
        if (!File.Exists(glue))
        {
            reason = $"glue script absent ({glue}) — run bench/third-party/fetch-subjects";
            return false;
        }
        if (!ProcessProbe.Exists("node", "--version", out string ver))
        {
            reason = "node not found";
            return false;
        }
        if (!Directory.Exists(BenchPaths.SfottyPackageDir))
        {
            reason = "@sfotty-pie/sfotty not fetched — run bench/third-party/fetch-subjects (needs npm)";
            return false;
        }
        reason = $"node {ver}";
        return true;
    }

    public AdapterResult Measure(BenchWorkload workload)
    {
        string glue = Path.Combine(BenchPaths.Glue, "sfotty_runner.mjs");
        // sfotty is in the bench cache's node_modules; point node's resolver there.
        string nodePath = Path.Combine(BenchPaths.Cache, "node_modules");
        var prev = Environment.GetEnvironmentVariable("NODE_PATH");
        Environment.SetEnvironmentVariable("NODE_PATH",
            string.IsNullOrEmpty(prev) ? nodePath : $"{nodePath}{Path.PathSeparator}{prev}");
        try
        {
            ProcessProbe.Exists("node", "--version", out string ver);
            return SubprocessRunner.Measure("node", [Quote(glue)], workload,
                versionNote: $"sfotty via node {ver}", measureCyclesForCap: JsW2MeasureCycles);
        }
        finally
        {
            Environment.SetEnvironmentVariable("NODE_PATH", prev);
        }
    }

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? $"\"{s}\"" : s;
}
