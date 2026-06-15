using CpuEmulator.Cpus.Z80;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M3.5-3a headline gate: the Z80 SingleStepTests sweep run THROUGH JittedCpu&lt;Z80Cpu&gt;
/// across ALL SEVEN planes (base/cb/ed/dd/fd/ddcb/fdcb). Every Z80 op falls back to inner.Step in 5-3a,
/// so the JIT final state MUST equal the interpreter's (which already passes the same vectors — PRs
/// #22–#28). A green sweep proves the GENERIC COMPILER (J1/J2/J3) — the discovery walk, the keyed
/// DescriptorFor, the per-CPU BlockDelegate, the data-driven register file, the cycle/budget/dispatcher
/// machinery — runs the complete Z80 faithfully BEFORE any Z80 IL is emitted (5-3b). The sweep also
/// proves the discovery walk handles every key shape: base, CB-prefixed, ED, DD/FD-prefixed, and the
/// compound DDCB/FDCB 4-byte forms. Sampled at CI scale (CPUEMULATOR_TOMHARTE_SAMPLE / 200 default);
/// CPUEMULATOR_UAT=full runs EVERY case through the JIT. The diff covers the full state (registers incl.
/// F's X/Y, WZ, Q, IM, Iff1/Iff2), RAM, ports, and cycle COUNT — fastmem-on bypasses the per-T-state bus
/// trace (Ground truth E), the same scope the 6502 JIT sweep asserts.</summary>
public class Z80JitTomHarteTests(ITestOutputHelper output)
{
    private static int ResolveSample()
    {
        if (Environment.GetEnvironmentVariable("CPUEMULATOR_UAT") == "full") return int.MaxValue;
        return int.TryParse(Environment.GetEnvironmentVariable("CPUEMULATOR_TOMHARTE_SAMPLE"),
            out int p) && p > 0 ? p : 200;
    }

    /// <summary>Load the plane's vector file, drive each sampled case through the JIT, and assert tier
    /// parity. Shared by all seven plane theories (the only per-plane difference is the file name).</summary>
    private void SweepPlane(string fileName, string label)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, fileName);
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = Z80TomHarteLoader.LoadFile(path);

        int sample = ResolveSample();
        int run = 0;
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (run >= sample) break;
            run++;
            if (Z80TomHarteRunner.RunCaseThroughJit(c) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break;
            }
        }

        output.WriteLine($"{label}: ran {run} (Z80 JIT)");
        Assert.True(run > 0, "no cases ran — sampling/skip logic is broken");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} Z80 JIT parity failure(s) of {run}:\n\n" +
                        string.Join("\n---\n", failures));
    }

    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredBasePlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Base_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"{opcode:x2}.json", $"{opcode:x2}");

    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Cb_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"cb {opcode:x2}.json", $"cb {opcode:x2}");

    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredEdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Ed_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"ed {opcode:x2}.json", $"ed {opcode:x2}");

    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredDdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Dd_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"dd {opcode:x2}.json", $"dd {opcode:x2}");

    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredFdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Fd_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"fd {opcode:x2}.json", $"fd {opcode:x2}");

    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredDdCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void DdCb_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"dd cb __ {opcode:x2}.json", $"dd cb __ {opcode:x2}");

    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredFdCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void FdCb_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"fd cb __ {opcode:x2}.json", $"fd cb __ {opcode:x2}");
}
