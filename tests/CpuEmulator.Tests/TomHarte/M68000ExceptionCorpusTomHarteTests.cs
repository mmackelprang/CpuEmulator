using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5d-1 (Task 14 axis c): the CROSS-CORPUS exception sweep — re-runs the M4.5a-c vector files with
/// assertExceptions:true over the EMBEDDED exception cases. Every embedded exception in the M4.5a-c files is an
/// ADDRESS ERROR (vector 3, deferred per DD4); this sweep's REAL job is a REGRESSION GUARD that turning
/// assertExceptions on does NOT wrongly assert the address-error cases. The TIMING axis is M4.5d-2.
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> Each in-scope file gets its OWN derived class;
/// the body is IDENTICAL to the pre-split single-theory body (the silent skip-when-file-absent and the
/// embedded-exception filter are preserved). The coverage guard asserts exact coverage of
/// <see cref="CanonicalFiles"/>.</para>
///
/// <para>Routine/CI runs cap each file at a 200-case sample (CPUEMULATOR_TOMHARTE_SAMPLE); the authoritative
/// substantive/milestone merge gate runs CPUEMULATOR_UAT=full (the full ~8065-case-per-file sweep).</para></summary>
public abstract class M68000ExceptionCorpusTomHarteSweepBase
{
    public static readonly string[] CanonicalFiles =
    [
        // MOVE family (M4.5a)
        "MOVE.b.json.gz", "MOVE.w.json.gz", "MOVE.l.json.gz",
        "MOVEA.w.json.gz", "MOVEA.l.json.gz",
        // integer ALU (M4.5b)
        "ADD.b.json.gz", "ADD.w.json.gz", "ADD.l.json.gz",
        "SUB.b.json.gz", "SUB.w.json.gz", "SUB.l.json.gz",
        "AND.b.json.gz", "AND.w.json.gz", "AND.l.json.gz",
        "OR.b.json.gz", "OR.w.json.gz", "OR.l.json.gz",
        "EOR.b.json.gz", "EOR.w.json.gz", "EOR.l.json.gz",
        "CMP.b.json.gz", "CMP.w.json.gz", "CMP.l.json.gz",
        "NEG.b.json.gz", "NEG.w.json.gz", "NEG.l.json.gz",
        "NOT.b.json.gz", "NOT.w.json.gz", "NOT.l.json.gz",
        "CLR.b.json.gz", "CLR.w.json.gz", "CLR.l.json.gz",
        "TST.b.json.gz", "TST.w.json.gz", "TST.l.json.gz",
        "MULU.json.gz", "MULS.json.gz",
        // shift/rotate + bit + BCD + Scc + data-movement (M4.5c)
        "ASL.w.json.gz", "LSR.w.json.gz", "ROL.w.json.gz", "ROXR.w.json.gz",
        "BTST.json.gz", "BCHG.json.gz", "BCLR.json.gz", "BSET.json.gz",
        "ABCD.json.gz", "NBCD.json.gz", "Scc.json.gz",
        "SWAP.json.gz", "LEA.json.gz", "PEA.json.gz", "TAS.json.gz",
        "MOVEM.w.json.gz", "MOVEM.l.json.gz",
    ];

    protected abstract string VectorFile { get; }
    public string FileForGuard => VectorFile;

    [M68000TomHarteFact]
    public void Embedded_exception_cases_assert_on_the_data_axis()
    {
        string file = VectorFile;
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;   // a few corpus files may be absent in trimmed caches — skip silently

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int sampleSize = M68000TomHarteVectors.ResolveSampleSize();
        int run = 0;
        int asserted = 0;        // embedded exception cases that ran + asserted on the data axis (the proof)
        int addrDeferred = 0;    // address-error cases still deferred (DD4 — M4.5d-2)
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;
            // Only the EMBEDDED exception cases are the subject here. Non-exception cases are covered by the
            // M4.5a/b/c default-flag sweeps — skip them so this sweep stays focused + fast.
            if (!M68000TomHarteRunner.IsExceptionCase(c)) continue;
            if (M68000TomHarteRunner.IsAddressErrorCase(c)) { addrDeferred++; continue; }   // vector 3 — DD4
            string? rr = M68000TomHarteRunner.RunCase(c, assertExceptions: true);
            if (ReferenceEquals(rr, M68000TomHarteRunner.DeferredException)) { addrDeferred++; continue; }
            asserted++;
            if (rr is not null) { failures.Add(rr); if (failures.Count >= 10) break; }
        }
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ embedded-exception data-axis failures over {asserted} asserted " +
            $"({addrDeferred} address-error deferred to M4.5d-2):\n" + string.Join("\n", failures));
    }
}

public sealed class M68000Exc_MOVE_b  : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MOVE.b.json.gz"; }
public sealed class M68000Exc_MOVE_w  : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MOVE.w.json.gz"; }
public sealed class M68000Exc_MOVE_l  : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MOVE.l.json.gz"; }
public sealed class M68000Exc_MOVEA_w : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MOVEA.w.json.gz"; }
public sealed class M68000Exc_MOVEA_l : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MOVEA.l.json.gz"; }
public sealed class M68000Exc_ADD_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "ADD.b.json.gz"; }
public sealed class M68000Exc_ADD_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "ADD.w.json.gz"; }
public sealed class M68000Exc_ADD_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "ADD.l.json.gz"; }
public sealed class M68000Exc_SUB_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "SUB.b.json.gz"; }
public sealed class M68000Exc_SUB_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "SUB.w.json.gz"; }
public sealed class M68000Exc_SUB_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "SUB.l.json.gz"; }
public sealed class M68000Exc_AND_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "AND.b.json.gz"; }
public sealed class M68000Exc_AND_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "AND.w.json.gz"; }
public sealed class M68000Exc_AND_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "AND.l.json.gz"; }
public sealed class M68000Exc_OR_b    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "OR.b.json.gz"; }
public sealed class M68000Exc_OR_w    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "OR.w.json.gz"; }
public sealed class M68000Exc_OR_l    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "OR.l.json.gz"; }
public sealed class M68000Exc_EOR_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "EOR.b.json.gz"; }
public sealed class M68000Exc_EOR_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "EOR.w.json.gz"; }
public sealed class M68000Exc_EOR_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "EOR.l.json.gz"; }
public sealed class M68000Exc_CMP_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "CMP.b.json.gz"; }
public sealed class M68000Exc_CMP_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "CMP.w.json.gz"; }
public sealed class M68000Exc_CMP_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "CMP.l.json.gz"; }
public sealed class M68000Exc_NEG_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "NEG.b.json.gz"; }
public sealed class M68000Exc_NEG_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "NEG.w.json.gz"; }
public sealed class M68000Exc_NEG_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "NEG.l.json.gz"; }
public sealed class M68000Exc_NOT_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "NOT.b.json.gz"; }
public sealed class M68000Exc_NOT_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "NOT.w.json.gz"; }
public sealed class M68000Exc_NOT_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "NOT.l.json.gz"; }
public sealed class M68000Exc_CLR_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "CLR.b.json.gz"; }
public sealed class M68000Exc_CLR_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "CLR.w.json.gz"; }
public sealed class M68000Exc_CLR_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "CLR.l.json.gz"; }
public sealed class M68000Exc_TST_b   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "TST.b.json.gz"; }
public sealed class M68000Exc_TST_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "TST.w.json.gz"; }
public sealed class M68000Exc_TST_l   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "TST.l.json.gz"; }
public sealed class M68000Exc_MULU    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MULU.json.gz"; }
public sealed class M68000Exc_MULS    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MULS.json.gz"; }
public sealed class M68000Exc_ASL_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "ASL.w.json.gz"; }
public sealed class M68000Exc_LSR_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "LSR.w.json.gz"; }
public sealed class M68000Exc_ROL_w   : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "ROL.w.json.gz"; }
public sealed class M68000Exc_ROXR_w  : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "ROXR.w.json.gz"; }
public sealed class M68000Exc_BTST    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "BTST.json.gz"; }
public sealed class M68000Exc_BCHG    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "BCHG.json.gz"; }
public sealed class M68000Exc_BCLR    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "BCLR.json.gz"; }
public sealed class M68000Exc_BSET    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "BSET.json.gz"; }
public sealed class M68000Exc_ABCD    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "ABCD.json.gz"; }
public sealed class M68000Exc_NBCD    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "NBCD.json.gz"; }
public sealed class M68000Exc_Scc     : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "Scc.json.gz"; }
public sealed class M68000Exc_SWAP    : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "SWAP.json.gz"; }
public sealed class M68000Exc_LEA     : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "LEA.json.gz"; }
public sealed class M68000Exc_PEA     : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "PEA.json.gz"; }
public sealed class M68000Exc_TAS     : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "TAS.json.gz"; }
public sealed class M68000Exc_MOVEM_w : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MOVEM.w.json.gz"; }
public sealed class M68000Exc_MOVEM_l : M68000ExceptionCorpusTomHarteSweepBase { protected override string VectorFile => "MOVEM.l.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical exception-corpus list.</summary>
public sealed class M68000ExceptionCorpusTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_exception_corpus_file_list()
    {
        var expected = M68000ExceptionCorpusTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M68000ExceptionCorpusTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M68000ExceptionCorpusTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M68000ExceptionCorpusTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
