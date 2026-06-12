using CpuEmulator.Cpus.Mos6502;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

public class Mos6502TomHarteTests(ITestOutputHelper output)
{
    /// <summary>
    /// ADC/SBC opcodes — decimal-mode (D set) cases are skipped in 3b-i because
    /// the emitter is binary-only until 3b-ii lands BCD (recorded plan deviation).
    /// </summary>
    private static readonly HashSet<byte> s_decimalSensitive =
    [
        0x69, 0x65, 0x75, 0x6D, 0x7D, 0x79, 0x61, 0x71,   // ADC
        0xE9, 0xE5, 0xF5, 0xED, 0xFD, 0xF9, 0xE1, 0xF1,   // SBC
    ];

    /// <summary>
    /// Implemented = present in the generated dispatch, probed reflection-free
    /// via the generated disassembler ("???" = not in the table).
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

        int run = 0, skippedDecimal = 0;
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            if (run >= sampleSize) break;
            if (s_decimalSensitive.Contains(opcode) && (testCase.Initial.P & 0x08) != 0)
            {
                skippedDecimal++; // binary-only ADC/SBC until 3b-ii
                continue;
            }
            run++;
            if (TomHarteRunner.RunCase(testCase) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break; // enough signal; don't flood the log
            }
        }

        // One telemetry line per opcode, green or red — Task 10's UAT gate records
        // total cases executed + decimal-skip counts in the PR body, and a passing
        // run must still surface those numbers (review finding, 3b-i tasks 8-9).
        output.WriteLine($"{opcode:x2}: ran {run}, skipped-decimal {skippedDecimal}");

        Assert.True(run > 0, "no cases ran — sampling/skip logic is broken");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} failing case(s) shown of {run} run" +
                        $" ({skippedDecimal} decimal-mode cases skipped):\n\n" +
                        string.Join("\n---\n", failures));
    }
}
