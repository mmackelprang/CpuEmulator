using CpuEmulator.Cpus.Z80;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M3.4a Task 8 — the base-plane TomHarte gate: the framework's first non-6502 ISA executes against
/// its exhaustive correctness oracle. Each COVERED base-plane opcode (probed reflection-free via the
/// generated disassembler, "???" = not in the table) runs its SingleStepTests/z80 vectors; the diff
/// covers registers (incl. F's undocumented X/Y bits 3/5), RAM, the separate ports array, and the
/// per-T-state bus trace. CPUEMULATOR_UAT=full runs ALL cases; otherwise a 200/opcode sample.
/// </summary>
public class Z80TomHarteTests(ITestOutputHelper output)
{
    /// <summary>The covered base-plane opcodes — present in the generated dispatch (Disassemble !=
    /// "???"). EXCLUDES the prefix bytes 0xCB/0xED/0xDD/0xFD (later planes) and the 4 deferred
    /// rotate-accumulator opcodes (0x07/0x0F/0x17/0x1F → M3.4b, no Insn row so Disassemble == "???").</summary>
    public static TheoryData<byte> CoveredBasePlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int opcode = 0; opcode <= 0xFF; opcode++)
            if (opcode is not (0xCB or 0xED or 0xDD or 0xFD)
                && Z80Cpu.Disassemble((byte)opcode, 0, 0) != "???")
                data.Add((byte)opcode);
        return data;
    }

    [Z80TomHarteTheory]
    [MemberData(nameof(CoveredBasePlaneOpcodes))]
    public void Opcode_matches_TomHarte_vectors(byte opcode)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"{opcode:x2}.json");
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = Z80TomHarteLoader.LoadFile(path);

        bool uatFull = Environment.GetEnvironmentVariable("CPUEMULATOR_UAT") == "full";
        int sampleSize = uatFull ? int.MaxValue
            : int.TryParse(Environment.GetEnvironmentVariable("CPUEMULATOR_TOMHARTE_SAMPLE"),
                           out int parsed) && parsed > 0 ? parsed : 200;
        // Staged gate (Ground truth G): registers-only unless the FULL trace is requested.
        bool registersOnly = Environment.GetEnvironmentVariable("CPUEMULATOR_Z80_REGS_ONLY") == "1";
        // M3.4b: the 4 rotate-accumulators (0x07/0x0F/0x17/0x1F) maintain Q and leave WZ invariant, so
        // they ride the full Q/WZ check; the rest of the base plane keeps the M3.4a posture (no Q/WZ —
        // its shared-class ops do not maintain Q and CALL/JP/… write WZ, which is M3.4c).
        bool checkInternal = opcode is 0x07 or 0x0F or 0x17 or 0x1F;

        int run = 0;
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            if (run >= sampleSize) break;
            run++;
            if (Z80TomHarteRunner.RunCase(testCase, registersOnly, checkInternal) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break;
            }
        }

        output.WriteLine($"{opcode:x2}: ran {run}");
        Assert.True(run > 0, "no cases ran — sampling/skip logic is broken");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} failing case(s) shown of {run} run:\n\n" +
                        string.Join("\n---\n", failures));
    }

    /// <summary>The covered CB-plane opcodes — every CB second byte present in the generated dispatch.
    /// All 256 are covered (the plane is total). Probed reflection-free via the prefixed disassembler
    /// key (0xCB00 | op); "???" = not in the table.</summary>
    public static TheoryData<byte> CoveredCbPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0; op <= 0xFF; op++)
            if (Z80Cpu.Disassemble((uint)(0xCB00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }

    [Z80TomHarteTheory]
    [MemberData(nameof(CoveredCbPlaneOpcodes))]
    public void Cb_opcode_matches_TomHarte_vectors(byte opcode)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"cb {opcode:x2}.json");   // NOTE the space in the filename
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = Z80TomHarteLoader.LoadFile(path);

        bool uatFull = Environment.GetEnvironmentVariable("CPUEMULATOR_UAT") == "full";
        int sampleSize = uatFull ? int.MaxValue
            : int.TryParse(Environment.GetEnvironmentVariable("CPUEMULATOR_TOMHARTE_SAMPLE"),
                           out int parsed) && parsed > 0 ? parsed : 200;
        bool registersOnly = Environment.GetEnvironmentVariable("CPUEMULATOR_Z80_REGS_ONLY") == "1";

        int run = 0;
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            if (run >= sampleSize) break;
            run++;
            // CB ops maintain Q and leave WZ invariant — the full M3.4b check.
            if (Z80TomHarteRunner.RunCase(testCase, registersOnly, checkInternal: true) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break;
            }
        }

        output.WriteLine($"cb {opcode:x2}: ran {run}");
        Assert.True(run > 0, "no cases ran — sampling/skip logic is broken");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} failing case(s) shown of {run} run:\n\n" +
                        string.Join("\n---\n", failures));
    }
}
