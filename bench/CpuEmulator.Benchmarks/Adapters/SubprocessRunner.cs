using System.Globalization;

namespace CpuEmulator.Benchmarks.Adapters;

/// <summary>Shared plumbing for the subprocess adapters (py65, fake6502, sfotty): write the workload
/// image to a temp file, run the subject's glue runner, parse its <c>CYCLES n</c> / <c>WALL_SECONDS
/// f</c> lines, and turn the result into an <see cref="AdapterResult"/>. A subprocess that fails to
/// launch, exits non-zero, parks at a non-trap PC (its runner exits non-zero on that), or fails to
/// print parseable lines yields Ran=false with the captured reason — never a crash, never a
/// fast-but-wrong number.</summary>
internal static class SubprocessRunner
{
    /// <summary>Run the subject's glue and produce a measured (or skipped) result. The workload's
    /// termination + window args are passed positionally: image-path, startPc, mode (trap|cap),
    /// trapPc, measureCycles.</summary>
    public static AdapterResult Measure(string exe, IEnumerable<string> leadingArgs, BenchWorkload w,
                                        string versionNote, long measureCyclesForCap)
    {
        string imagePath = Path.Combine(Path.GetTempPath(),
            $"cpuemu-bench-{Guid.NewGuid():N}.bin");
        try
        {
            File.WriteAllBytes(imagePath, w.Image);
            // Third-party subjects run a BOUNDED cycle WINDOW (cap mode) on BOTH workloads — cycles/sec
            // is a rate, so a representative slice is a fair measurement, and it keeps pure-Python py65
            // from running the full 96M-cycle Klaus for hours. (Our own two tiers run W1 to the trap;
            // they can afford it. The methodology doc states this slice-rate honestly.) For W1 the
            // window is min(measureCyclesForCap, the anchor); for W2 it is the kernel's cap.
            long measure = w.FixedCycleCap is long cap
                ? Math.Min(measureCyclesForCap, cap)
                : Math.Min(measureCyclesForCap, w.ExpectedCycles);
            var args = new List<string>(leadingArgs)
            {
                Quote(imagePath),
                w.StartPc.ToString(CultureInfo.InvariantCulture),
                "cap",                                  // a bounded window — see above
                w.SuccessTrapPc.ToString(CultureInfo.InvariantCulture),
                measure.ToString(CultureInfo.InvariantCulture),
            };
            string argLine = string.Join(' ', args);

            var (ok, stdout, stderr) = ProcessProbe.Run(exe, argLine, TimeSpan.FromMinutes(20));
            if (!ok)
                return AdapterResult.Skipped(
                    $"subject runner failed: {FirstLine(stderr) ?? FirstLine(stdout) ?? "non-zero exit"}");

            if (!TryParse(stdout, out long cycles, out double wall))
                return AdapterResult.Skipped($"unparseable runner output: {FirstLine(stdout) ?? "(empty)"}");

            // The cycle count the subject reports uses its OWN cycle model (cross-emulator models
            // differ legitimately on edge cases); cycles/sec is the rate over the warmed window. A
            // subject that crashed/diverged terminates early — its runner exits non-zero (caught
            // above) or reports a near-zero window, which the report shows transparently.
            if (cycles <= 0 || wall <= 0)
                return AdapterResult.Skipped("subject produced no measurable window (likely diverged)");
            return AdapterResult.Measured(cycles, wall, versionNote);
        }
        catch (Exception ex)
        {
            return AdapterResult.Skipped($"adapter error: {ex.Message}");
        }
        finally
        {
            try { File.Delete(imagePath); } catch { /* best effort */ }
        }
    }

    private static bool TryParse(string stdout, out long cycles, out double wall)
    {
        cycles = 0; wall = 0;
        bool gotCycles = false, gotWall = false;
        foreach (string line in stdout.Split('\n'))
        {
            string t = line.Trim();
            if (t.StartsWith("CYCLES ", StringComparison.Ordinal)
                && long.TryParse(t.AsSpan(7), NumberStyles.Integer, CultureInfo.InvariantCulture, out cycles))
                gotCycles = true;
            else if (t.StartsWith("WALL_SECONDS ", StringComparison.Ordinal)
                && double.TryParse(t.AsSpan(13), NumberStyles.Float, CultureInfo.InvariantCulture, out wall))
                gotWall = true;
        }
        return gotCycles && gotWall;
    }

    private static string? FirstLine(string s) =>
        s.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();

    private static string Quote(string s) => s.Contains(' ', StringComparison.Ordinal) ? $"\"{s}\"" : s;
}
