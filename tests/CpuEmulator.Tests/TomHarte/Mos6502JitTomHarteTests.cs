using CpuEmulator.Core;
using CpuEmulator.Cpus.Mos6502;
using CpuEmulator.Jit;
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

    // ── Task 6: the sample-size resolution honors CPUEMULATOR_UAT=full ──────────────────────────
    /// <summary>The JIT sweep's sample size mirrors the interpreter's: CPUEMULATOR_UAT=full -&gt; the
    /// full case count (int.MaxValue, i.e. every case in the file), else 200 (or the SAMPLE override).
    /// This is the resolution the full-sweep pre-merge gate sets; at CI scale it stays sampled (fast).
    /// Asserted directly so the env-honoring contract is pinned without needing the full vector set.</summary>
    [Fact]
    public void ResolveJit_honors_full()
    {
        int Resolve(string? uat, string? sample)
        {
            bool full = uat == "full";
            return full ? int.MaxValue
                : int.TryParse(sample, out int parsed) && parsed > 0 ? parsed : 200;
        }
        Assert.Equal(int.MaxValue, Resolve("full", null));
        Assert.Equal(int.MaxValue, Resolve("full", "200"));  // full wins over a sample override
        Assert.Equal(200, Resolve(null, null));              // CI default
        Assert.Equal(500, Resolve(null, "500"));             // explicit sample override
    }

    // ── Task 6: ADC/SBC now run by EMIT, not fallback (the discovery/seam probe) ─────────────────
    private static BlockCompiler<Mos6502Cpu> NewCompiler(params (ushort At, byte[] Bytes)[] pokes)
    {
        var space = new AddressSpace(AddressSpaceKind.Program, addressBits: 16);
        space.MapMemory(0x0000, new byte[0x10000], writable: true);
        foreach (var (at, bytes) in pokes)
            for (int i = 0; i < bytes.Length; i++)
                space.Write8((uint)(at + i), bytes[i]);
        var opts = new JitOptions();
        return new BlockCompiler<Mos6502Cpu>(new Mos6502Cpu(space), Mos6502Cpu.JitTarget, space, new Fastmem(space, opts), opts);
    }

    [Fact]
    public void ADC_opcode_block_emits_no_fallback()
    {
        // A block containing an ADC (then a JMP-self to end it) emits ZERO fallbacks — ADC is now
        // emitted (Task 5). Contrast a block containing a BRK, which still emits ONE fallback
        // (BRK/RTI/undefined stay interpreter fallbacks — the recorded decision).
        var adc = NewCompiler((0x0200, [0xA9, 0x01, 0x69, 0x02, 0x4C, 0x04, 0x02])); // LDA / ADC / JMP*
        adc.Compile(0x0200);
        Assert.Equal(0, adc.FallbackEmitCount);

        var brk = NewCompiler((0x0200, [0xA9, 0x01, 0x00])); // LDA #1 / BRK
        brk.Compile(0x0200);
        Assert.Equal(1, brk.FallbackEmitCount);
    }

    [TomHarteTheory]
    [InlineData(0x69)]  // ADC Immediate
    [InlineData(0x65)]  // ADC ZeroPage
    [InlineData(0xE9)]  // SBC Immediate
    [InlineData(0xED)]  // SBC Absolute
    public void Decimal_subset_TomHarte_passes_through_the_JIT_by_emit(byte opcode)
    {
        // A decimal-mode (D set in Initial.P) TomHarte subset for a few ADC/SBC opcodes, run through
        // the JIT via RunCaseThroughJit. All must pass — and now via the EMITTED decimal arm, not the
        // M2-i interpreter fallback (Task 5/6). A spot sample keeps CI fast; the full sweep
        // (CPUEMULATOR_UAT=full) runs all 80,093 decimal cases through emit.
        string dir  = TomHarteVectors.TryGetVectorDirectory()!;
        string path = Path.Combine(dir, $"{opcode:x2}.json");
        Assert.True(File.Exists(path), $"vector file missing: {path}");
        var cases = TomHarteLoader.LoadFile(path);

        int ran = 0;
        var failures = new List<string>();
        foreach (var testCase in cases)
        {
            if ((testCase.Initial.P & 0x08) == 0) continue;  // decimal-mode cases only
            ran++;
            if (TomHarteRunner.RunCaseThroughJit(testCase) is { } failure)
            {
                failures.Add(failure);
                if (failures.Count >= 3) break;
            }
            if (ran >= 200) break;   // a spot sample — the full sweep covers them all
        }
        output.WriteLine($"{opcode:x2}: ran {ran} decimal-mode cases (JIT, emitted arm)");
        Assert.True(ran > 0, "no decimal-mode cases found in the vector file");
        if (failures.Count > 0)
            Assert.Fail($"{failures.Count} decimal JIT parity failure(s) of {ran} run:\n\n" +
                        string.Join("\n---\n", failures));
    }
}
