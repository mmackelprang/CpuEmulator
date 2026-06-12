using CpuEmulator.Cpus.Mos6502;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

public class Mos6502TomHarteTests(ITestOutputHelper output)
{
    /// <summary>
    /// Implemented = present in the generated dispatch, probed reflection-free
    /// via the generated disassembler ("???" = not in the table).
    /// All cases run, including decimal-mode (D set) ADC/SBC — BCD landed in 3b-ii.
    /// </summary>
    public static TheoryData<byte> ImplementedOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int opcode = 0; opcode <= 0xFF; opcode++)
            if (Mos6502Cpu.Disassemble((byte)opcode, 0, 0) != "???")
                data.Add((byte)opcode);
        return data;
    }

    [TomHarteTheory]
    [MemberData(nameof(ImplementedOpcodes))]
    public void Opcode_matches_TomHarte_vectors(byte opcode)
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
