namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>kstenerud/Musashi (the fast native C Motorola 680x0 interpreter used by MAME-class
/// projects, MIT) via a compiled native shim run as a subprocess — the 68000 HEAD-TO-HEAD C
/// reference (mirrors <see cref="Z80CAdapter"/> for the Z80). Probes for a C compiler (cc/gcc/clang)
/// + the fetched Musashi source + the authored C harness; runs Musashi's m68kmake codegen + compiles
/// the harness once (cached) and runs it. Skips-with-note when the compiler or source is absent
/// (identical to Z80CAdapter's behavior). The 68000 workloads run in <c>cap</c> mode (UsesCpmBdos
/// false); the runner drives by an instruction budget and prints CYCLES + INSTRUCTIONS + WALL_SECONDS,
/// so this row carries guest-MIPS — the cross-CPU-comparable headline (the 68000 cycle axis is partial
/// on `main`). Its <see cref="Name"/> matches the cited registry subject exactly, so when ANY
/// head-to-head 68000 ref runs the merged comparison-table generator promotes this measured row over
/// the cited placeholder automatically (no generator change needed).</summary>
public sealed class MusashiAdapter : IEmulatorAdapter
{
    // The bounded measured window, in INSTRUCTIONS (the runner treats the SubprocessRunner cap as an
    // instruction budget so the head-to-head instructions/sec is directly comparable to our tiers'
    // frozen instruction caps). Native C is fast — a large window is fine.
    private const long MusashiMeasureWindow = 50_000_000;
    private string _cc = "";

    public string Name => "Musashi (C)";

    public bool Probe(out string reason)
    {
        string harness = Path.Combine(BenchPaths.Glue, "musashi_runner.c");
        if (!File.Exists(harness))
        {
            reason = $"C harness absent ({harness})";
            return false;
        }
        string shim = Path.Combine(BenchPaths.Glue, "mamesf.h");
        if (!File.Exists(shim))
        {
            reason = $"softfloat shim absent ({shim})";
            return false;
        }
        if (!File.Exists(BenchPaths.MusashiSource))
        {
            reason = $"Musashi source not fetched ({BenchPaths.MusashiSource}) — run bench/third-party/fetch-subjects";
            return false;
        }
        foreach (string cc in new[] { "cc", "gcc", "clang" })
        {
            if (ProcessProbe.Exists(cc, "--version", out string v))
            {
                _cc = cc;
                reason = string.IsNullOrEmpty(v) ? cc : v;
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
            string glueDir = BenchPaths.Glue;
            string harness = Path.Combine(glueDir, "musashi_runner.c");
            string shim = Path.Combine(glueDir, "mamesf.h");
            string musashiDir = Path.GetDirectoryName(BenchPaths.MusashiSource)!;   // <cache>/musashi
            string softfloatC = Path.Combine(musashiDir, "softfloat", "softfloat.c");

            string m68kcpuC = BenchPaths.MusashiSource;                  // m68kcpu.c
            string m68kdasmC = Path.Combine(musashiDir, "m68kdasm.c");
            string m68kInC = Path.Combine(musashiDir, "m68k_in.c");
            string m68kmakeC = Path.Combine(musashiDir, "m68kmake.c");
            string m68kmakeExe = Path.Combine(musashiDir,
                OperatingSystem.IsWindows() ? "m68kmake.exe" : "m68kmake");
            string opsH = Path.Combine(musashiDir, "m68kops.h");
            string opsC = Path.Combine(musashiDir, "m68kops.c");

            string exe = Path.Combine(BenchPaths.Cache, "musashi",
                OperatingSystem.IsWindows() ? "musashi_runner.exe" : "musashi_runner");

            // ── Codegen step (compile-once-cached): Musashi's m68kmake generates the opcode handler
            //    tables (m68kops.h/.c) from m68k_in.c. Regenerate if they are missing or stale vs.
            //    their codegen inputs. (m68kops.* are NOT fetched — they are generated here.) ──
            bool opsStale = !File.Exists(opsH) || !File.Exists(opsC)
                || File.GetLastWriteTimeUtc(m68kInC) > File.GetLastWriteTimeUtc(opsC)
                || File.GetLastWriteTimeUtc(m68kmakeC) > File.GetLastWriteTimeUtc(opsC);
            if (opsStale)
            {
                bool makeStale = !File.Exists(m68kmakeExe)
                    || File.GetLastWriteTimeUtc(m68kmakeC) > File.GetLastWriteTimeUtc(m68kmakeExe);
                if (makeStale)
                {
                    var (mkOk, _, mkErr) = ProcessProbe.Run(_cc,
                        $"-O2 -o \"{m68kmakeExe}\" \"{m68kmakeC}\"", TimeSpan.FromMinutes(2));
                    if (!mkOk)
                        return AdapterResult.Skipped(
                            $"m68kmake compile failed: {mkErr.Split('\n').FirstOrDefault()?.Trim() ?? "(no diagnostic)"}");
                }
                // m68kmake <outputDir> <m68k_in.c>: writes m68kops.h + m68kops.c into the musashi dir.
                var (genOk, _, genErr) = ProcessProbe.Run(m68kmakeExe,
                    $"\"{musashiDir}\" \"{m68kInC}\"", TimeSpan.FromMinutes(2));
                if (!genOk || !File.Exists(opsC))
                    return AdapterResult.Skipped(
                        $"m68kmake codegen failed: {genErr.Split('\n').FirstOrDefault()?.Trim() ?? "(no diagnostic)"}");
            }

            // ── Compile the runner once (rebuild if any input — the harness, the shim, the Musashi
            //    core, the generated ops, or softfloat — is newer than the cached binary). ──
            string[] inputs = { harness, shim, m68kcpuC, opsC, m68kdasmC, softfloatC };
            bool stale = !File.Exists(exe)
                || inputs.Any(p => File.Exists(p) && File.GetLastWriteTimeUtc(p) > File.GetLastWriteTimeUtc(exe));
            if (stale)
            {
                // -I<glueDir> so the mamesf.h shim resolves softfloat/milieu.h's #include "mamesf.h";
                // -I<musashiDir> so the harness #include "m68k.h" + the core's internal headers resolve.
                string compileArgs =
                    $"-O2 -I\"{glueDir}\" -I\"{musashiDir}\" -o \"{exe}\" \"{harness}\" " +
                    $"\"{m68kcpuC}\" \"{opsC}\" \"{m68kdasmC}\" \"{softfloatC}\"";
                var (ok, _, stderr) = ProcessProbe.Run(_cc, compileArgs, TimeSpan.FromMinutes(3));
                if (!ok)
                    return AdapterResult.Skipped(
                        $"compile failed: {stderr.Split('\n').FirstOrDefault()?.Trim() ?? "(no diagnostic)"}");
            }

            ProcessProbe.Exists(_cc, "--version", out string ver);
            string ccName = ver.Split('\n').FirstOrDefault()?.Trim() ?? _cc;
            return SubprocessRunner.Measure(exe, [], workload,
                versionNote: $"Musashi v4.60, built with {ccName}",
                measureCyclesForCap: MusashiMeasureWindow,
                bdosMode: false);   // the 68000 workloads are cap-mode; never CP/M BDOS
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"adapter error: {ex.Message}");
        }
    }
}
