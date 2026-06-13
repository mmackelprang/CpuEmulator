namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>py65 (pure-Python) via a python subprocess. Probes for the fetched py65 venv (or any
/// python with py65 importable) + the authored glue script; skips with the fetch/pip instruction
/// when absent. py65 is the slowest subject by orders of magnitude — an honest, interesting
/// cross-language data point, not a defect. To keep the wall-clock bounded, the W2 window is run for
/// a portable measured slice (cycles/sec is a rate, so a slice is a fair rate measurement).</summary>
public sealed class Py65Adapter : IEmulatorAdapter
{
    private const long Py65W2MeasureCycles = 2_000_000;   // a few seconds of pure-Python work
    private string _python = "";

    public string Name => "py65 (Python)";

    public bool Probe(out string reason)
    {
        string glue = Path.Combine(BenchPaths.Glue, "py65_runner.py");
        if (!File.Exists(glue))
        {
            reason = $"glue script absent ({glue}) — run bench/third-party/fetch-subjects";
            return false;
        }

        // Prefer the fetched venv; fall back to a system python with py65 importable.
        string? venvPy = BenchPaths.Py65VenvPython();
        if (venvPy is not null && ProcessProbe.Succeeds(venvPy, "-c \"import py65\""))
        {
            _python = venvPy;
            reason = ProcessProbe.Exists(venvPy, "--version", out string v) ? v : "py65 venv";
            return true;
        }
        if (ProcessProbe.Exists("python", "--version", out string sysVer))
        {
            if (ProcessProbe.Succeeds("python", "-c \"import py65\""))
            {
                _python = "python";
                reason = sysVer;
                return true;
            }
            reason = "py65 not installed — run: pip install py65 (or bench/third-party/fetch-subjects)";
            return false;
        }
        reason = "python not found";
        return false;
    }

    public AdapterResult Measure(BenchWorkload workload)
    {
        string glue = Path.Combine(BenchPaths.Glue, "py65_runner.py");
        ProcessProbe.Exists(_python, "--version", out string ver);
        return SubprocessRunner.Measure(_python, [Quote(glue)], workload,
            versionNote: $"py65, {ver}", measureCyclesForCap: Py65W2MeasureCycles);
    }

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? $"\"{s}\"" : s;
}
