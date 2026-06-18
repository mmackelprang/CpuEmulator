using CpuEmulator.Cpus.Z80;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M3.5-3a headline gate: the Z80 SingleStepTests sweep run THROUGH JittedCpu&lt;Z80Cpu&gt; across ALL
/// SEVEN planes (base/cb/ed/dd/fd/ddcb/fdcb). Every Z80 op falls back to inner.Step in 5-3a, so the JIT final
/// state MUST equal the interpreter's. Sampled at CI scale (CPUEMULATOR_TOMHARTE_SAMPLE / 200 default);
/// CPUEMULATOR_UAT=full runs EVERY case through the JIT.
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> The seven planes were formerly seven
/// <c>[Theory]</c> methods in this one class (one serial collection); they are now one shared base body with one
/// derived class per plane (each its own collection). The opcode-set generators are reused from
/// <see cref="Z80TomHarteTests"/>; the sampling + parity-diff logic is IDENTICAL to the pre-split body.</para></summary>
public abstract class Z80JitTomHartePlaneBase(ITestOutputHelper output)
{
    private static int ResolveSample() => TomHarteSampling.ResolveSampleSize();

    /// <summary>Load the plane's vector file, drive each sampled case through the JIT, and assert tier parity.</summary>
    protected void SweepPlane(string fileName, string label)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, fileName);
        Assert.True(File.Exists(path), $"vector file missing: {path}");

        int sample = ResolveSample();
        var cases = TomHarteCaches.Z80.Get(path, sample,
            max => Z80TomHarteLoader.LoadFile(path, max));
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
}

public sealed class Z80Jit_Base(ITestOutputHelper output) : Z80JitTomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredBasePlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Base_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"{opcode:x2}.json", $"{opcode:x2}");
}

public sealed class Z80Jit_Cb(ITestOutputHelper output) : Z80JitTomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Cb_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"cb {opcode:x2}.json", $"cb {opcode:x2}");
}

public sealed class Z80Jit_Ed(ITestOutputHelper output) : Z80JitTomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredEdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Ed_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"ed {opcode:x2}.json", $"ed {opcode:x2}");
}

public sealed class Z80Jit_Dd(ITestOutputHelper output) : Z80JitTomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredDdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Dd_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"dd {opcode:x2}.json", $"dd {opcode:x2}");
}

public sealed class Z80Jit_Fd(ITestOutputHelper output) : Z80JitTomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredFdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Fd_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"fd {opcode:x2}.json", $"fd {opcode:x2}");
}

public sealed class Z80Jit_DdCb(ITestOutputHelper output) : Z80JitTomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredDdCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void DdCb_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"dd cb __ {opcode:x2}.json", $"dd cb __ {opcode:x2}");
}

public sealed class Z80Jit_FdCb(ITestOutputHelper output) : Z80JitTomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredFdCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void FdCb_opcode_matches_TomHarte_through_the_JIT(byte opcode)
        => SweepPlane($"fd cb __ {opcode:x2}.json", $"fd cb __ {opcode:x2}");
}
