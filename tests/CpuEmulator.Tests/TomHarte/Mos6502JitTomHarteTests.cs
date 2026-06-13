using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// Task 7: sampled TomHarte SingleStepTests parity through the Tier-1 <c>JittedCpu</c>. Reuses
/// the interpreter test's implemented-opcode probe and the same per-opcode vector files, but
/// runs each case through a JIT-wrapped CPU (forcing block compilation + execution — Step would
/// just re-test the interpreter, so the runner drives Run with the case's cycle budget).
///
/// The assertion is state + RAM + cycle count, NOT the bus trace (fastmem bypasses the bus for
/// RAM/ROM — Ground truth E). ADC/SBC/BRK/RTI run through the interpreter-fallback path, which is
/// still a valid parity check: the JIT must produce the interpreter's result whether by emit or
/// by fallback. Trace-equivalence (DisableFastmem) is pinned by <c>JitTraceEquivalenceTests</c>.
///
/// Sampling: <c>CPUEMULATOR_UAT=full</c> runs all 10,000 cases/opcode (the M2-i UAT gate, also the
/// M2-ii full sweep); otherwise <c>CPUEMULATOR_TOMHARTE_SAMPLE</c> or the 200/opcode default.
/// </summary>
public class Mos6502JitTomHarteTests(ITestOutputHelper output)
{
    /// <summary>Same implemented-opcode probe as the interpreter test (Disassemble != "???").</summary>
    public static TheoryData<byte> ImplementedOpcodes() => Mos6502TomHarteTests.ImplementedOpcodes();

    [TomHarteTheory]
    [MemberData(nameof(ImplementedOpcodes))]
    public void Opcode_matches_TomHarte_through_the_JIT(byte opcode)
    {
        string dir  = TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"{opcode:x2}.json");
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = TomHarteLoader.LoadFile(path);

        bool uatFull   = Environment.GetEnvironmentVariable("CPUEMULATOR_UAT") == "full";
        int  sampleSize = uatFull ? int.MaxValue
            : int.TryParse(Environment.GetEnvironmentVariable("CPUEMULATOR_TOMHARTE_SAMPLE"),
                           out int parsed) && parsed > 0 ? parsed : 200;

        int run = 0;
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            if (run >= sampleSize) break;
            run++;
            if (TomHarteRunner.RunCaseThroughJit(testCase) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break; // enough signal; don't flood the log
            }
        }

        output.WriteLine($"{opcode:x2}: ran {run} (JIT)");

        Assert.True(run > 0, "no cases ran — sampling/skip logic is broken");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} JIT parity failure(s) of {run} run:\n\n" +
                        string.Join("\n---\n", failures));
    }
}
