namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>superzazu/z80 (the single-file cycle-accurate C Z80, MIT, ZEXALL/ZEXDOC-proven) via a
/// compiled native shim run as a subprocess — the Z80 cross-language C anchor (mirrors
/// <see cref="Fake6502Adapter"/> for the 6502). Probes for a C compiler (cc/gcc/clang) + the fetched
/// z80.c/z80.h source + the authored C harness; compiles the harness once (cached) and runs it.
/// Skips-with-note when the compiler or source is absent (identical to fake6502's behavior). For the
/// Z80-W1 ZEXDOC prefix it runs in <c>bdos</c> mode so the C runner services the CP/M BDOS CALL
/// host-side (fn-2/fn-9 + RET) + honors the warm-boot sentinel — the identical real ZEX code our
/// tiers run.</summary>
public sealed class Z80CAdapter : IEmulatorAdapter
{
    private const long Z80CMeasureCycles = 20_000_000;   // native C is fast — a large window is fine
    private string _cc = "";

    public string Name => "superzazu/z80 (C)";

    public bool Probe(out string reason)
    {
        string harness = Path.Combine(BenchPaths.Glue, "z80c_runner.c");
        if (!File.Exists(harness))
        {
            reason = $"C harness absent ({harness})";
            return false;
        }
        if (!File.Exists(BenchPaths.Z80CSource))
        {
            reason = $"z80.c not fetched ({BenchPaths.Z80CSource}) — run bench/third-party/fetch-subjects";
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
            string harness = Path.Combine(BenchPaths.Glue, "z80c_runner.c");
            string z80Dir = Path.GetDirectoryName(BenchPaths.Z80CSource)!;
            string z80Header = Path.Combine(z80Dir, "z80.h");
            string exe = Path.Combine(BenchPaths.Cache, "z80c",
                OperatingSystem.IsWindows() ? "z80c_runner.exe" : "z80c_runner");

            // Compile once (rebuild if the harness/source is newer than the cached binary).
            bool stale = !File.Exists(exe)
                || File.GetLastWriteTimeUtc(harness) > File.GetLastWriteTimeUtc(exe)
                || File.GetLastWriteTimeUtc(BenchPaths.Z80CSource) > File.GetLastWriteTimeUtc(exe)
                || (File.Exists(z80Header) && File.GetLastWriteTimeUtc(z80Header) > File.GetLastWriteTimeUtc(exe));
            if (stale)
            {
                // -I<z80Dir> so #include "z80.h"/"z80.c" resolve to the fetched source.
                string compileArgs = $"-O2 -I\"{z80Dir}\" -o \"{exe}\" \"{harness}\"";
                var (ok, _, stderr) = ProcessProbe.Run(_cc, compileArgs, TimeSpan.FromMinutes(2));
                if (!ok)
                    return AdapterResult.Skipped(
                        $"compile failed: {stderr.Split('\n').FirstOrDefault()?.Trim() ?? "(no diagnostic)"}");
            }

            ProcessProbe.Exists(_cc, "--version", out string ver);
            string ccName = ver.Split('\n').FirstOrDefault()?.Trim() ?? _cc;
            return SubprocessRunner.Measure(exe, [], workload,
                versionNote: $"superzazu/z80, built with {ccName}",
                measureCyclesForCap: Z80CMeasureCycles,
                bdosMode: workload.UsesCpmBdos);
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"adapter error: {ex.Message}");
        }
    }
}
