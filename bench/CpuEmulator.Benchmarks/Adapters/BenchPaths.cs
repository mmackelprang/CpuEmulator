namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>Locates the bench cache dir (third-party runtimes fetched by
/// bench/third-party/fetch-subjects) and the authored glue-script dir
/// (bench/third-party, the shim source committed in-repo). Both are overridable by env so a CI box
/// or a second machine can point at its own cache:
/// <list type="bullet">
/// <item><c>CPUEMULATOR_BENCHCACHE</c> — the fetched-subjects cache (default
/// ~/.cache/cpuemulator/bench).</item>
/// <item><c>CPUEMULATOR_BENCHGLUE</c> — the authored glue dir (default: probed upward from the
/// running assembly for <c>bench/third-party</c>).</item>
/// </list></summary>
internal static class BenchPaths
{
    public static string Cache =>
        Environment.GetEnvironmentVariable("CPUEMULATOR_BENCHCACHE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        ".cache", "cpuemulator", "bench");

    /// <summary>The authored glue dir (bench/third-party). Honors CPUEMULATOR_BENCHGLUE, else walks
    /// up from the assembly location looking for a <c>bench/third-party</c> dir (works from both the
    /// runner's bin dir and the test's bin dir). Returns the best guess even if absent — adapters
    /// File.Exists-probe the specific scripts and skip-with-note when missing.</summary>
    public static string Glue
    {
        get
        {
            string? env = Environment.GetEnvironmentVariable("CPUEMULATOR_BENCHGLUE");
            if (!string.IsNullOrEmpty(env)) return env;
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null)
            {
                string candidate = Path.Combine(dir.FullName, "bench", "third-party");
                if (Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }
            return Path.Combine(AppContext.BaseDirectory, "bench", "third-party");
        }
    }

    /// <summary>The Python interpreter inside the fetched py65 venv (Windows + POSIX layouts), or
    /// null if the venv is absent.</summary>
    public static string? Py65VenvPython()
    {
        string win = Path.Combine(Cache, "py65venv", "Scripts", "python.exe");
        if (File.Exists(win)) return win;
        string posix = Path.Combine(Cache, "py65venv", "bin", "python");
        return File.Exists(posix) ? posix : null;
    }

    public static string Fake6502Source => Path.Combine(Cache, "fake6502", "fake6502.c");
    public static string SfottyPackageDir => Path.Combine(Cache, "node_modules", "@sfotty-pie", "sfotty");

    /// <summary>The fetched superzazu/z80 single-file C source (the Z80 cross-language C anchor — the
    /// fake6502.c discipline: fetched-not-vendored). Both z80.c + z80.h live in &lt;cache&gt;/z80c.</summary>
    public static string Z80CSource => Path.Combine(Cache, "z80c", "z80.c");

    /// <summary>The fetched DrGoldfire/Z80.js single source file (the optional JS Z80 subject — the
    /// MIT GitHub core, fetched-not-vendored into &lt;cache&gt;/z80js/Z80.js by fetch-subjects; NOT the
    /// unrelated npm `z80` package). The runner loads it via node's vm.</summary>
    public static string Z80JsSource => Path.Combine(Cache, "z80js", "Z80.js");
}
