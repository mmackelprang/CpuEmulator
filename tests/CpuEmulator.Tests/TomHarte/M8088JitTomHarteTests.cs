using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using Xunit.Abstractions;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M5.6 headline gate (extended in M6): the 8086 SingleStepTests/8088 DATA-AXIS family sweep run THROUGH
/// JittedCpu&lt;M8086Cpu&gt;. In M5.6 this was all-fallback (every 8086 op deferred to inner.Step via the empty-Ops
/// NeedsFallback descriptors). As of M6 PR-B/PR-C the ALU + MOV families now EMIT real IL through the JIT — so for
/// those rows the sweep proves genuine EMIT parity (the compiled IL's final state == the oracle), not just
/// fallback-passthrough. Row MD (ROADMAP #4) added the F6/F7 /4../7 MUL/IMUL/DIV/IDIV rows; Row STR (ROADMAP #4)
/// added the string family (MOVS/CMPS/STOS/LODS/SCAS, A4-A7/AA-AF, REP-prefixed or not) — so the A4-AF files in
/// this sweep now prove genuine EMIT parity (the CX-loop + DF-direction + REPE/REPNE ZF early-exit), not
/// fallback-passthrough. Row II (ROADMAP #4) added the soft-interrupt family (CD INT imm8, CC INT3, CE INTO,
/// CF IRET) — so the CD/CC/CE/CF files now prove genuine EMIT parity (the IVT FLAGS:CS:IP push + IF/TF clear +
/// vector, INTO's OF gate, IRET's pop + reserved-bit forcing), not fallback-passthrough. BOUND (62/63) stays
/// fallback (out of #4 scope). The remaining still-fallback ops are the control/stack tail not in #4's scope, where the
/// JIT final state equals the interpreter's (which already passes these vectors — M5.5a–d). A green sweep proves the GENERIC COMPILER (the discovery walk, the keyed DescriptorFor,
/// the per-CPU BlockDelegate, the data-driven register map, the cycle/budget/dispatch machinery) runs the
/// complete 8086 faithfully — the same proof M4.6 delivered for the 68000, now on the 16-bit-register /
/// 20-bit-segmented-address / little-endian / byte-variable-length-decode CPU. The data axis is the 14 registers
/// (FLAGS mask-aware) + the changed RAM cells.
///
/// <para>It honors the SAME divide-error + IDIV-sign-quirk deferrals the interpreter family sweeps disclose
/// (never faked green): a FAILING DIV/IDIV (/6 /7) or AAM (D4) case is run, then classified — if the only
/// discrepancy is the documented undefined-flag fallout (DD6, IsDivideErrorUndefinedFlagsOnly) or the IDIV
/// quotient-sign quirk (IsIdivSignQuirk), it is counted-deferred; anything else is a real tier-parity failure
/// the gate surfaces. Because the JIT result == the interpreter result in all-fallback, the classifiers (which
/// re-run the interpreter to confirm the quirk shape) correctly classify the JIT discrepancy. Sampled at CI
/// scale; CPUEMULATOR_UAT=full runs every case through the JIT.</para>
///
/// <para>Lever-3 split: the sweep is one xUnit COLLECTION per partition (the 8 sealed M8088JitTom_P0..P7
/// derived classes below), so the heaviest JIT tier parallelizes across the configured threads, mirroring the
/// interpreter split (Mos6502TomHarteSweepBase). The sampling + classify logic is IDENTICAL to the pre-split
/// single-class body.</para></summary>
public abstract class M8088JitSweepBase(ITestOutputHelper output)
{
    // private (not protected): RunFile lives in this base, so derived classes never touch s_metadata
    // directly — and a protected member of a public type cannot expose the internal M8088Metadata
    // (CS0052). Mirrors the interpreter ALU base (M8088AluTomHarteSweepBase.s_metadata is private).
    private static readonly M8088Metadata s_metadata =
        M8088Metadata.Load(M8088TomHarteVectors.TryGetVectorDirectory());

    /// <summary>Partition the data-axis file list into <paramref name="parts"/> stripes; return stripe
    /// <paramref name="index"/>. Stripe assignment is by position (i % parts) so each stripe is a balanced mix.</summary>
    public static TheoryData<string> Partition(int index, int parts)
    {
        var data = new TheoryData<string>();
        int i = 0;
        foreach (var f in M8088DataAxisCorpus.Files)
        {
            if (i % parts == index) data.Add(f);
            i++;
        }
        return data;
    }

    protected void RunFile(string file)
    {
        string? dir = M8088TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        // Skip-when-absent for an individual file (the canonical list is broad; a missing file is tolerated).
        if (!File.Exists(path)) return;

        // Parse opcode hex = segment before the FIRST '.', and the reg subfield from a NN.R.json.gz name
        // (copied verbatim from M8088AluTomHarteSweepBase so the per-subgroup flags-mask + deferral classes match).
        int firstDot = file.IndexOf('.');
        string opcodeHex = file[..firstDot];
        int? regField = null;
        string rest = file[(firstDot + 1)..];                       // after the opcode (e.g. "6.json.gz" or "json.gz")
        int restDot = rest.IndexOf('.');
        string firstSeg = restDot >= 0 ? rest[..restDot] : rest;
        if (int.TryParse(firstSeg, out int rf)) regField = rf;       // a NN.R.* group file ⇒ R is the reg subfield

        bool isF6F7 = opcodeHex.Equals("F6", StringComparison.OrdinalIgnoreCase)
                      || opcodeHex.Equals("F7", StringComparison.OrdinalIgnoreCase);
        bool width16 = opcodeHex.Equals("F7", StringComparison.OrdinalIgnoreCase);
        bool isDivGroup = isF6F7 && regField is 6 or 7;   // DIV (/6) + IDIV (/7)
        bool isIdiv = isF6F7 && regField == 7;            // the signed-division quotient-sign quirk lives here
        bool isAam = opcodeHex.Equals("D4", StringComparison.OrdinalIgnoreCase);

        int sampleSize = M8088TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M8088.Get(path, sampleSize,
            max => M8088TomHarteLoader.LoadFile(path, max, parseCycles: false));   // data axis: skip carried cycles
        int run = 0, executed = 0, deferredDivideError = 0, deferredIdivSignQuirk = 0;
        var failures = new List<string>();
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;

            // Run ONE instruction through Tier-1 JittedCpu<M8086Cpu>, then classify a failure exactly as the
            // interpreter ALU sweep does. In M6 the ALU + MOV rows emit real IL (the sweep proves EMIT parity for
            // them). Row MD (ROADMAP #4) now also EMITS the F6/F7 /4../7 MUL/IMUL/DIV/IDIV rows — so the DIV/IDIV
            // divide-error + IDIV-sign-quirk classifiers below now classify an EMITTED discrepancy (the emit is
            // byte-identical to the interpreter, so the classifier's re-run-the-interpreter confirmation still
            // holds: emit == interpreter, the quirk shape is unchanged). AAM (D4) still falls back (out of #4
            // scope) — its divide-error deferral is unchanged. The DIVIDE-ERROR → INT0 push is MODELED; the
            // silicon's UNDEFINED arithmetic flags from the aborted division (DD6) and the IDIV quotient-sign quirk
            // are the documented genuinely-resistant classes — counted-deferred only after the classifier confirms
            // the discrepancy is PRECISELY that quirk (every other register + RAM byte exact).
            executed++;
            string? res = M8088TomHarteRunner.RunCaseThroughJit(c, s_metadata, opcodeHex, regField);
            if (res is not null)
            {
                if ((isDivGroup || isAam) && M8088TomHarteRunner.IsDivideErrorUndefinedFlagsOnly(c))
                {
                    deferredDivideError++;
                    executed--;   // a counted DD6 deferral, not a clean executed pass
                    continue;
                }
                if (isIdiv && M8088TomHarteRunner.IsIdivSignQuirk(c, width16))
                {
                    deferredIdivSignQuirk++;
                    executed--;   // not a clean executed pass; it's a counted deferral
                    continue;
                }
                failures.Add(res);
                if (failures.Count >= 10) break;
            }
        }

        output.WriteLine($"{file}: ran {run}, executed {executed}, deferred-divide {deferredDivideError}, " +
                         $"deferred-idiv {deferredIdivSignQuirk} (8086 JIT)");
        Assert.True(executed > 0,
            $"{file}: 0 executed cases — the gate would be vacuous (deferred divide-error: {deferredDivideError}, " +
            $"deferred IDIV sign-quirk: {deferredIdivSignQuirk})");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ tier-parity failure(s) over {executed} executed cases " +
            $"(deferred divide-error: {deferredDivideError}, deferred IDIV sign-quirk: {deferredIdivSignQuirk}):\n" +
            string.Join("\n", failures));
    }
}

public sealed class M8088JitTom_P0(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(0, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P1(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(1, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P2(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(2, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P3(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(3, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P4(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(4, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P5(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(5, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P6(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(6, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }

public sealed class M8088JitTom_P7(ITestOutputHelper o) : M8088JitSweepBase(o)
{ public static TheoryData<string> Files() => Partition(7, 8);
  [M8088TomHarteTheory][MemberData(nameof(Files))] public void Tier_parity_through_the_JIT(string f) => RunFile(f); }
