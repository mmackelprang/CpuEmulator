using System;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>The ONE per-file case-sample resolver for every TomHarte sweep (6502/Z80/680x0/8088, interpreter and
/// JIT). Replaces the six duplicated copies (the three *TomHarteVectors.cs / Z80JitTomHarteTests.cs helpers and
/// the three inline 6502-interp/6502-JIT/Z80-interp resolvers). Routine/CI caps the per-file case loop at
/// CPUEMULATOR_TOMHARTE_SAMPLE (default 100 — lowered from 200 in PR-T1, lever 7); CPUEMULATOR_UAT=full removes
/// the cap (int.MaxValue) so the authoritative milestone gate runs the full per-file sweep. Caps the per-file
/// case loop ONLY — it does NOT change which files run, which cases are deferred/filtered, or what is asserted.</summary>
internal static class TomHarteSampling
{
    /// <summary>The routine-path default. Lowered 200 → 100 (lever 7): a 2x faster fast path; the exhaustive
    /// gate is still CPUEMULATOR_UAT=full.</summary>
    public const int DefaultSample = 100;

    /// <summary>Reads the two env vars and resolves the cap. Public per-arg overload (no env read) so it is unit
    /// testable without mutating process-global env (which would race the parallel vector-gated theories).</summary>
    public static int ResolveSampleSize(string? uat, string? sample)
    {
        if (uat == "full") return int.MaxValue;
        return int.TryParse(sample, out int p) && p > 0 ? p : DefaultSample;
    }

    public static int ResolveSampleSize() => ResolveSampleSize(
        Environment.GetEnvironmentVariable("CPUEMULATOR_UAT"),
        Environment.GetEnvironmentVariable("CPUEMULATOR_TOMHARTE_SAMPLE"));
}
