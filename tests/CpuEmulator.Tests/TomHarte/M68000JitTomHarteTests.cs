using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.6 headline gate: the 680x0 SingleStepTests DATA-AXIS sweep run THROUGH JittedCpu&lt;M68000Cpu&gt;
/// (all-fallback). Every 68000 op falls back to inner.Step in M4, so the JIT final state MUST equal the
/// interpreter's (which already passes these vectors — M4.5a–d-1). A green sweep proves the GENERIC COMPILER
/// (the discovery walk, the keyed DescriptorFor, the per-CPU BlockDelegate, the data-driven register map, the
/// cycle/budget/dispatch machinery) runs the complete 68000 faithfully — the same proof 5-3a delivered for the
/// Z80, now on the 32-bit-register / 24-bit-address / big-endian / word-decode CPU. The data axis is
/// D0–D7/A0–A6/USP/SSP/SR/RAM (fastmem bypasses the per-transaction bus trace, the same scope the 6502/Z80 JIT
/// sweeps assert). The TIMING axis (final.pc/prefetch/trace/cycle) is gated on M4.5d-2 and is NOT asserted here.
/// Exception cases assert via assertExceptions:true (the synchronous vector is handled by the fallback valve);
/// the address-error (vector 3) large frame stays deferred (DD4). The corpus-artifact cases the interpreter
/// sweeps exclude (the ASL.b inconsistent-register-shift vectors + the CHK in-range UNPREDICTABLE-CCR cases)
/// are excluded identically here via M68000DataAxisCorpus.IsExcludedCase, so the JIT corpus is identical in
/// EXECUTED cases to the interpreter corpus. Sampled at CI scale; CPUEMULATOR_UAT=full runs every case through
/// the JIT.
///
/// <para>Lever-3 split: the sweep is one xUnit COLLECTION per partition (the 8 sealed M68000JitTom_P0..P7
/// derived classes below), so the heaviest JIT tier parallelizes across the configured threads, mirroring the
/// interpreter split (Mos6502TomHarteSweepBase). The exclusion + deferral logic is IDENTICAL to the pre-split
/// single-class body.</para></summary>
public abstract class M68000JitSweepBase(ITestOutputHelper output)
{
    /// <summary>Partition the data-axis file list into <paramref name="parts"/> stripes; return stripe
    /// <paramref name="index"/>. Stripe assignment is by position (i % parts) so each stripe is a balanced mix.</summary>
    public static TheoryData<string> Partition(int index, int parts)
    {
        var data = new TheoryData<string>();
        int i = 0;
        foreach (var f in M68000DataAxisCorpus.Files) { if (i % parts == index) data.Add(f); i++; }
        return data;
    }

    protected void RunFile(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        Assert.True(File.Exists(path), $"vector file missing: {path}");

        int sample = M68000TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M68000.Get(path, sample,
            max => M68000TomHarteLoader.LoadFile(path, max));
        int run = 0, executed = 0, deferred = 0, excluded = 0;
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (run >= sample) break;
            run++;
            // Carry the interpreter data-axis sweeps' corpus-artifact exclusions forward (Refinement 3): the
            // ASL.b inconsistent-register-shift vectors + the CHK in-range UNPREDICTABLE-CCR cases. Because
            // RunCaseThroughJit runs the same interpreter via the all-fallback valve, an excluded case would
            // produce the SAME "failure" the interpreter sweeps avoid by skipping. Excluded BEFORE the run.
            if (M68000DataAxisCorpus.IsExcludedCase(c)) { excluded++; continue; }
            var rr = M68000TomHarteRunner.RunCaseThroughJit(c, assertExceptions: true);
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 5) break; }
        }
        output.WriteLine($"{file}: ran {run}, executed {executed}, deferred {deferred}, excluded {excluded} (68000 JIT)");
        Assert.True(executed > 0, $"{file}: 0 executed cases — the gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count} tier-parity failure(s) of {executed} executed:\n" +
            string.Join("\n---\n", failures));
    }
}

public sealed class M68000JitTom_P0(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(0, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P1(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(1, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P2(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(2, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P3(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(3, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P4(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(4, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P5(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(5, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P6(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(6, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M68000JitTom_P7(ITestOutputHelper o) : M68000JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(7, 8);
  [M68000TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }
