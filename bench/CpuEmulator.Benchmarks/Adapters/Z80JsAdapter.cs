namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>DrGoldfire/Z80.js (Molly Howell's MIT JS Z80) via a node subprocess — the OPTIONAL Z80
/// cross-language JS subject (mirrors <see cref="JsEmulatorAdapter"/>/sfotty for the 6502). Probes
/// for node + the fetched Z80.js source + the authored glue script; skips-with-note when absent (it
/// NEVER gates Milestone A). For the Z80-W1 ZEXDOC prefix it runs in <c>bdos</c> mode so the JS
/// runner services the CP/M BDOS CALL host-side (fn-9 + RET) + honors the warm-boot sentinel.
/// <para>NOTE on the cycle model: Z80.js is an instruction interpreter using the documented
/// per-opcode T-state counts (its own README states it is not gate-level cycle-accurate). That is a
/// legitimate own cycle model — the report labels all Z80 third-party rows "indicative
/// cross-language", and the divergence/stall gate in SubprocessRunner catches a jammed run.</para></summary>
public sealed class Z80JsAdapter : IEmulatorAdapter
{
    private const long Z80JsMeasureCycles = 20_000_000;   // V8 is fast; a larger slice is fine

    public string Name => "Z80.js (JavaScript/Node)";

    public bool Probe(out string reason)
    {
        string glue = Path.Combine(BenchPaths.Glue, "z80js_runner.mjs");
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
        if (!File.Exists(BenchPaths.Z80JsSource))
        {
            reason = "DrGoldfire/Z80.js not fetched — run bench/third-party/fetch-subjects (needs network)";
            return false;
        }
        reason = $"node {ver}";
        return true;
    }

    public AdapterResult Measure(BenchWorkload workload)
    {
        string glue = Path.Combine(BenchPaths.Glue, "z80js_runner.mjs");
        ProcessProbe.Exists("node", "--version", out string ver);
        return SubprocessRunner.Measure("node", [Quote(glue)], workload,
            versionNote: $"DrGoldfire/Z80.js via node {ver}",
            measureCyclesForCap: Z80JsMeasureCycles,
            bdosMode: workload.UsesCpmBdos);
    }

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? $"\"{s}\"" : s;
}
