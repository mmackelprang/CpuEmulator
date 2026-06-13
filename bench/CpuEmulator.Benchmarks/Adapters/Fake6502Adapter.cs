namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>fake6502 (Mike Chambers' single-file portable C 6502) via a compiled native shim run as
/// a subprocess. Probes for a C compiler (gcc/clang/cc) + the fetched fake6502.c source + the
/// authored C harness; compiles the harness once (cached) and runs it. Skips-with-note when the
/// compiler or source is absent. Subprocess (not P/Invoke) keeps the interop trivial + portable —
/// the recorded simplest realization (P/Invoke is the noted alternative).</summary>
public sealed class Fake6502Adapter : IEmulatorAdapter
{
    private const long FakeW2MeasureCycles = 20_000_000;   // native C is fast
    private string _cc = "";

    public string Name => "fake6502 (C)";

    public bool Probe(out string reason)
    {
        string harness = Path.Combine(BenchPaths.Glue, "fake6502_runner.c");
        if (!File.Exists(harness))
        {
            reason = $"C harness absent ({harness})";
            return false;
        }
        if (!File.Exists(BenchPaths.Fake6502Source))
        {
            reason = $"fake6502.c not fetched ({BenchPaths.Fake6502Source}) — run bench/third-party/fetch-subjects";
            return false;
        }
        foreach (string cc in new[] { "cc", "gcc", "clang" })
        {
            if (ProcessProbe.Exists(cc, "--version", out _))
            {
                _cc = cc;
                reason = ProcessProbe.Exists(cc, "--version", out string v) ? v : cc;
                return true;
            }
        }
        reason = "no C compiler found (cc/gcc/clang) — install a toolchain to populate this row";
        return false;
    }

    public AdapterResult Measure(BenchWorkload workload)
    {
        try
        {
            string harness = Path.Combine(BenchPaths.Glue, "fake6502_runner.c");
            string fakeDir = Path.GetDirectoryName(BenchPaths.Fake6502Source)!;
            string exe = Path.Combine(BenchPaths.Cache, "fake6502",
                OperatingSystem.IsWindows() ? "fake6502_runner.exe" : "fake6502_runner");

            // Compile once (rebuild if the harness/source is newer than the cached binary).
            bool stale = !File.Exists(exe)
                || File.GetLastWriteTimeUtc(harness) > File.GetLastWriteTimeUtc(exe)
                || File.GetLastWriteTimeUtc(BenchPaths.Fake6502Source) > File.GetLastWriteTimeUtc(exe);
            if (stale)
            {
                // -I<fakeDir> so #include "fake6502.c"/.h resolve to the fetched source; NMOS6502 +
                // DECIMALMODE match our NMOS-with-BCD emulator (the fair comparison).
                string compileArgs =
                    $"-O2 -DNMOS6502 -DDECIMALMODE -I\"{fakeDir}\" -o \"{exe}\" \"{harness}\"";
                var (ok, _, stderr) = ProcessProbe.Run(_cc, compileArgs, TimeSpan.FromMinutes(2));
                if (!ok)
                    return AdapterResult.Skipped(
                        $"compile failed: {stderr.Split('\n').FirstOrDefault()?.Trim() ?? "(no diagnostic)"}");
            }

            ProcessProbe.Exists(_cc, "--version", out string ver);
            string ccName = ver.Split('\n').FirstOrDefault()?.Trim() ?? _cc;
            return SubprocessRunner.Measure(exe, [], workload,
                versionNote: $"fake6502, built with {ccName}", measureCyclesForCap: FakeW2MeasureCycles);
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"adapter error: {ex.Message}");
        }
    }
}
