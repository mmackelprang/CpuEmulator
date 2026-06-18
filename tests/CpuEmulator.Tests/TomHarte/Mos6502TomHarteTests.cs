using CpuEmulator.Cpus.Mos6502;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// The 6502 SingleStepTests sweep. Implemented opcodes are probed reflection-free via the generated disassembler
/// ("???" = not in the table). All cases run, including decimal-mode (D set) ADC/SBC.
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> The single per-opcode <c>[Theory]</c> was one
/// xUnit collection (all ~150 opcodes serial on one thread). It is now split into 4 index-partitioned collections
/// (<see cref="Mos6502Tom_P0"/>…<see cref="Mos6502Tom_P3"/>) that distribute across cores. Each opcode maps to
/// exactly ONE partition (<c>index % 4</c>), so the union is the full implemented set and the row count is
/// unchanged. <see cref="ImplementedOpcodes"/> stays here (the JIT sweep reuses it).</para>
/// </summary>
public static class Mos6502TomHarteTests
{
    /// <summary>Implemented = present in the generated dispatch (Disassemble != "???").</summary>
    public static TheoryData<byte> ImplementedOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int opcode = 0; opcode <= 0xFF; opcode++)
            if (Mos6502Cpu.Disassemble((byte)opcode, 0, 0) != "???")
                data.Add((byte)opcode);
        return data;
    }

    /// <summary>The implemented opcodes whose discovery index ≡ <paramref name="k"/> (mod <paramref name="n"/>).
    /// The n partitions are disjoint and their union is the full implemented set — the per-opcode row count is
    /// preserved across the split.</summary>
    public static TheoryData<byte> PartitionOpcodes(int k, int n)
    {
        var data = new TheoryData<byte>();
        int idx = 0;
        for (int opcode = 0; opcode <= 0xFF; opcode++)
            if (Mos6502Cpu.Disassemble((byte)opcode, 0, 0) != "???")
            {
                if (idx % n == k) data.Add((byte)opcode);
                idx++;
            }
        return data;
    }
}

/// <summary>Shared per-opcode 6502 interpreter sweep body. One derived class per partition (its own collection).
/// The sampling + diff logic is IDENTICAL to the pre-split single-theory body.</summary>
public abstract class Mos6502TomHarteSweepBase(ITestOutputHelper output)
{
    protected void RunOpcode(byte opcode)
    {
        string dir  = TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"{opcode:x2}.json");
        Assert.True(File.Exists(path), $"vector file missing: {path}");

        int sampleSize = TomHarteSampling.ResolveSampleSize();
        var cases = TomHarteCaches.Mos6502.Get(path, sampleSize,
            max => TomHarteLoader.LoadFile(path, max));

        int run = 0;
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            if (run >= sampleSize) break;
            run++;
            if (TomHarteRunner.RunCase(testCase) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break; // enough signal; don't flood the log
            }
        }

        output.WriteLine($"{opcode:x2}: ran {run}");

        Assert.True(run > 0, "no cases ran — sampling/skip logic is broken");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} failing case(s) shown of {run} run:\n\n" +
                        string.Join("\n---\n", failures));
    }
}

public sealed class Mos6502Tom_P0(ITestOutputHelper output) : Mos6502TomHarteSweepBase(output)
{
    public static TheoryData<byte> Ops() => Mos6502TomHarteTests.PartitionOpcodes(0, 4);
    [TomHarteTheory][MemberData(nameof(Ops))] public void Opcode_matches_TomHarte_vectors(byte opcode) => RunOpcode(opcode);
}

public sealed class Mos6502Tom_P1(ITestOutputHelper output) : Mos6502TomHarteSweepBase(output)
{
    public static TheoryData<byte> Ops() => Mos6502TomHarteTests.PartitionOpcodes(1, 4);
    [TomHarteTheory][MemberData(nameof(Ops))] public void Opcode_matches_TomHarte_vectors(byte opcode) => RunOpcode(opcode);
}

public sealed class Mos6502Tom_P2(ITestOutputHelper output) : Mos6502TomHarteSweepBase(output)
{
    public static TheoryData<byte> Ops() => Mos6502TomHarteTests.PartitionOpcodes(2, 4);
    [TomHarteTheory][MemberData(nameof(Ops))] public void Opcode_matches_TomHarte_vectors(byte opcode) => RunOpcode(opcode);
}

public sealed class Mos6502Tom_P3(ITestOutputHelper output) : Mos6502TomHarteSweepBase(output)
{
    public static TheoryData<byte> Ops() => Mos6502TomHarteTests.PartitionOpcodes(3, 4);
    [TomHarteTheory][MemberData(nameof(Ops))] public void Opcode_matches_TomHarte_vectors(byte opcode) => RunOpcode(opcode);
}
