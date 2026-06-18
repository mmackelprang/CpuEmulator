using CpuEmulator.Cpus.Z80;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M3.4a Task 8 — the base-plane TomHarte gate: the framework's first non-6502 ISA executes against its
/// exhaustive correctness oracle. Each COVERED opcode (probed reflection-free via the generated disassembler,
/// "???" = not in the table) runs its SingleStepTests/z80 vectors; the diff covers registers (incl. F's
/// undocumented X/Y bits 3/5), RAM, the separate ports array, and the per-T-state bus trace.
/// CPUEMULATOR_UAT=full runs ALL cases; otherwise a 200/opcode sample.
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> The seven planes (base/cb/ed/dd/fd/ddcb/fdcb)
/// were formerly seven <c>[Theory]</c> methods in THIS one class — one xUnit collection, so all ~1,500 covered
/// opcodes ran serially on one thread. They are now one shared base body (<see cref="Z80TomHartePlaneBase"/>)
/// with one derived class PER PLANE (each its own collection), so the planes distribute across cores. The
/// per-plane <c>CoveredXxxPlaneOpcodes</c> generators stay HERE (the JIT sweep references them via
/// <c>MemberType = typeof(Z80TomHarteTests)</c>); the row count is unchanged.</para>
/// </summary>
public static class Z80TomHarteTests
{
    /// <summary>The covered base-plane opcodes — present in the generated dispatch (Disassemble != "???").
    /// EXCLUDES the prefix bytes 0xCB/0xED/0xDD/0xFD and the 4 deferred rotate-accumulator opcodes.</summary>
    public static TheoryData<byte> CoveredBasePlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int opcode = 0; opcode <= 0xFF; opcode++)
            if (opcode is not (0xCB or 0xED or 0xDD or 0xFD)
                && Z80Cpu.Disassemble((byte)opcode, 0, 0) != "???")
                data.Add((byte)opcode);
        return data;
    }

    /// <summary>The covered CB-plane opcodes — all 256 (the plane is total). Probed via (0xCB00 | op).</summary>
    public static TheoryData<byte> CoveredCbPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0; op <= 0xFF; op++)
            if (Z80Cpu.Disassemble((uint)(0xCB00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }

    /// <summary>The covered ED opcodes — ED core (0x40–0x7F) + ED block ops (0xA0–0xBB). Probed via (0xED00 | op).</summary>
    public static TheoryData<byte> CoveredEdPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x40; op <= 0x7F; op++)
            if (Z80Cpu.Disassemble((uint)(0xED00 | op), 0, 0) != "???")
                data.Add((byte)op);
        for (int op = 0xA0; op <= 0xBB; op++)
            if (Z80Cpu.Disassemble((uint)(0xED00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }

    /// <summary>The covered DD-core opcodes (0x00–0xFF minus the prefix bytes). Probed via (0xDD00 | op).</summary>
    public static TheoryData<byte> CoveredDdPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x00; op <= 0xFF; op++)
        {
            if (op is 0xCB or 0xDD or 0xED or 0xFD) continue;   // prefix bytes — no standalone core vector
            if (Z80Cpu.Disassemble((uint)(0xDD00 | op), 0, 0) != "???")
                data.Add((byte)op);
        }
        return data;
    }

    /// <summary>The covered FD-core opcodes — the IY analogue of <see cref="CoveredDdPlaneOpcodes"/>.</summary>
    public static TheoryData<byte> CoveredFdPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x00; op <= 0xFF; op++)
        {
            if (op is 0xCB or 0xDD or 0xED or 0xFD) continue;
            if (Z80Cpu.Disassemble((uint)(0xFD00 | op), 0, 0) != "???")
                data.Add((byte)op);
        }
        return data;
    }

    /// <summary>The covered DDCB-compound opcodes — all 256 final opcodes are vectored. Probed via (0xDDCB00 | op).</summary>
    public static TheoryData<byte> CoveredDdCbPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x00; op <= 0xFF; op++)
            if (Z80Cpu.Disassemble((uint)(0xDDCB00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }

    /// <summary>The covered FDCB-compound opcodes — the IY analogue. Probed via (0xFDCB00 | op).</summary>
    public static TheoryData<byte> CoveredFdCbPlaneOpcodes()
    {
        var data = new TheoryData<byte>();
        for (int op = 0x00; op <= 0xFF; op++)
            if (Z80Cpu.Disassemble((uint)(0xFDCB00 | op), 0, 0) != "???")
                data.Add((byte)op);
        return data;
    }
}

/// <summary>Shared body for the per-plane Z80 interpreter TomHarte sweep. One derived class per plane (its own
/// xUnit collection). The sampling + diff logic is IDENTICAL to the pre-split per-theory body — only the
/// collection boundary changed (M3.4c: the universal final Q/WZ/IM check).</summary>
public abstract class Z80TomHartePlaneBase(ITestOutputHelper output)
{
    protected void RunPlane(byte opcode, string fileName, string label)
    {
        string dir = Z80TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, fileName);
        Assert.True(File.Exists(path), $"vector file missing: {path}");

        int sampleSize = TomHarteSampling.ResolveSampleSize();
        var cases = TomHarteCaches.Z80.Get(path, sampleSize,
            max => Z80TomHarteLoader.LoadFile(path, max));
        bool registersOnly = Environment.GetEnvironmentVariable("CPUEMULATOR_Z80_REGS_ONLY") == "1";

        int run = 0;
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            if (run >= sampleSize) break;
            run++;
            if (Z80TomHarteRunner.RunCase(testCase, registersOnly) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break;
            }
        }

        output.WriteLine($"{label}: ran {run}");
        Assert.True(run > 0, "no cases ran — sampling/skip logic is broken");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} failing case(s) shown of {run} run:\n\n" +
                        string.Join("\n---\n", failures));
    }
}

public sealed class Z80Tom_Base(ITestOutputHelper output) : Z80TomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredBasePlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Opcode_matches_TomHarte_vectors(byte opcode) => RunPlane(opcode, $"{opcode:x2}.json", $"{opcode:x2}");
}

public sealed class Z80Tom_Cb(ITestOutputHelper output) : Z80TomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Cb_opcode_matches_TomHarte_vectors(byte opcode) => RunPlane(opcode, $"cb {opcode:x2}.json", $"cb {opcode:x2}");
}

public sealed class Z80Tom_Ed(ITestOutputHelper output) : Z80TomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredEdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Ed_opcode_matches_TomHarte_vectors(byte opcode) => RunPlane(opcode, $"ed {opcode:x2}.json", $"ed {opcode:x2}");
}

public sealed class Z80Tom_Dd(ITestOutputHelper output) : Z80TomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredDdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Dd_opcode_matches_TomHarte_vectors(byte opcode) => RunPlane(opcode, $"dd {opcode:x2}.json", $"dd {opcode:x2}");
}

public sealed class Z80Tom_Fd(ITestOutputHelper output) : Z80TomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredFdPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void Fd_opcode_matches_TomHarte_vectors(byte opcode) => RunPlane(opcode, $"fd {opcode:x2}.json", $"fd {opcode:x2}");
}

public sealed class Z80Tom_DdCb(ITestOutputHelper output) : Z80TomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredDdCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void DdCb_opcode_matches_TomHarte_vectors(byte opcode) => RunPlane(opcode, $"dd cb __ {opcode:x2}.json", $"dd cb __ {opcode:x2}");
}

public sealed class Z80Tom_FdCb(ITestOutputHelper output) : Z80TomHartePlaneBase(output)
{
    [Z80TomHarteTheory]
    [MemberData(nameof(Z80TomHarteTests.CoveredFdCbPlaneOpcodes), MemberType = typeof(Z80TomHarteTests))]
    public void FdCb_opcode_matches_TomHarte_vectors(byte opcode) => RunPlane(opcode, $"fd cb __ {opcode:x2}.json", $"fd cb __ {opcode:x2}");
}
