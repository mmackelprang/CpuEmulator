using System.Diagnostics;

namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>Cheap, side-effect-free process probes for the third-party adapters' Probe() checks.
/// All swallow exceptions and return false on any failure — a Probe must NEVER throw (the
/// graceful-degradation contract).</summary>
internal static class ProcessProbe
{
    /// <summary>True if <paramref name="exe"/> runs with <paramref name="args"/> and exits 0;
    /// captures the first stdout line in <paramref name="firstLine"/> (e.g. a --version string).</summary>
    public static bool Exists(string exe, string args, out string firstLine)
    {
        firstLine = "";
        try
        {
            var (ok, stdout, _) = Run(exe, args, TimeSpan.FromSeconds(10));
            if (!ok) return false;
            firstLine = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            return true;
        }
        catch { return false; }
    }

    /// <summary>True if <paramref name="exe"/> <paramref name="args"/> exits 0 (a yes/no check).</summary>
    public static bool Succeeds(string exe, string args)
    {
        try { return Run(exe, args, TimeSpan.FromSeconds(20)).Ok; }
        catch { return false; }
    }

    /// <summary>Run a process to completion (bounded by <paramref name="timeout"/>); returns whether
    /// it exited 0 and its captured stdout/stderr. Throws only on a launch failure the caller wraps.</summary>
    public static (bool Ok, string Stdout, string Stderr) Run(string exe, string args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start {exe}");
        string stdout = p.StandardOutput.ReadToEnd();
        string stderr = p.StandardError.ReadToEnd();
        if (!p.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return (false, stdout, stderr);
        }
        return (p.ExitCode == 0, stdout, stderr);
    }
}
