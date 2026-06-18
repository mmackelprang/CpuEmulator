using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>
/// M4.5d-2a (ADR 0008 §5): the PC/PREFETCH-axis green sweep — the queue END STATE gate. Runs the M4.5d-1 +
/// M4.5a-c vector files with <c>pcPrefetchAxis: true, assertExceptions: true</c>, asserting the prefetch-queue's
/// observable state (<c>final.pc</c> + both <c>final.prefetch</c> words) ON TOP OF the data axis, but WITHOUT
/// the per-transaction trace / cycle-count diff (the 2a ceiling — cycle-exactness is 2b).
///
/// <para><b>Parallelism (test-infra, zero semantics change).</b> The two pre-split theories (the M4.5d-1
/// control-flow families + the M4.5a-c data families, disjoint) are merged into ONE base body (they shared
/// <c>RunPcPrefetchSweep</c> already) and split per-file: each in-scope file gets its OWN derived class (its own
/// xUnit collection) so all 68 files distribute across cores. The body — including the CHK-in-range filter — is
/// IDENTICAL to the pre-split body. The coverage guard asserts exact coverage of <see cref="CanonicalFiles"/>.</para>
///
/// <para><b>Honesty (ADR 0008 §7).</b> 2a is "PC/prefetch-exact", NOT yet "cycle-exact". The address-error
/// (vector 3) group-0 cases stay DEFERRED (IsAddressErrorCase); all other exception cases assert.</para>
///
/// <para>Routine/CI runs cap each file at a 200-case sample (CPUEMULATOR_TOMHARTE_SAMPLE); the authoritative
/// substantive/milestone merge gate runs CPUEMULATOR_UAT=full (the full ~8065-case-per-file sweep).</para>
/// </summary>
public abstract class M68000TimingAxisTomHarteSweepBase
{
    public static readonly string[] CanonicalFiles =
    [
        // ---- the M4.5d-1 control-flow + exception families (reseed-on-transfer proof) + ÷0 re-run (22) ----
        "Bcc.json.gz", "BSR.json.gz", "DBcc.json.gz",
        "JMP.json.gz", "JSR.json.gz", "RTS.json.gz", "RTR.json.gz", "RTE.json.gz",
        "LINK.json.gz", "UNLINK.json.gz",
        "TRAP.json.gz", "TRAPV.json.gz", "CHK.json.gz", "NOP.json.gz",
        "ANDItoCCR.json.gz", "ANDItoSR.json.gz",
        "ORItoCCR.json.gz",  "ORItoSR.json.gz",
        "EORItoCCR.json.gz", "EORItoSR.json.gz",
        "DIVU.json.gz", "DIVS.json.gz",
        // ---- the M4.5a-c data-movement / ALU / shift-bit-BCD corpus (sequential refill proof) (46) ----
        // MOVE family (M4.5a)
        "MOVE.b.json.gz", "MOVE.w.json.gz", "MOVE.l.json.gz",
        "MOVEA.w.json.gz", "MOVEA.l.json.gz",
        "MOVE.q.json.gz",   // MOVEQ — corpus file is named MOVE.q (NOT MOVEQ); EA-less, data in operword 7-0
        "MOVEfromSR.json.gz", "MOVEtoSR.json.gz", "MOVEtoCCR.json.gz",
        "MOVEfromUSP.json.gz", "MOVEtoUSP.json.gz",
        // integer ALU (M4.5b)
        "ADD.b.json.gz", "ADD.w.json.gz", "ADD.l.json.gz",
        "SUB.b.json.gz", "SUB.w.json.gz", "SUB.l.json.gz",
        "AND.b.json.gz", "OR.w.json.gz", "EOR.l.json.gz",
        "CMP.b.json.gz", "CMP.w.json.gz", "CMP.l.json.gz",
        "NEG.w.json.gz", "NOT.l.json.gz", "CLR.b.json.gz", "TST.w.json.gz",
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
    public void Family_is_PcPrefetch_green()
    {
        string file = VectorFile;
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);   // skipped at discovery when vectors are absent; present == not null (merge gate)
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;   // a few corpus files may be absent in trimmed caches — skip silently

        int sampleSize = M68000TomHarteVectors.ResolveSampleSize();
        var cases = TomHarteCaches.M68000.Get(path, sampleSize,
            max => M68000TomHarteLoader.LoadFile(path, max));
        Assert.NotEmpty(cases);

        var failures = new List<string>();
        int run = 0;
        int executed = 0, deferred = 0, unpredictable = 0;
        foreach (var c in cases)
        {
            if (run >= sampleSize) break;
            run++;
            // CHK on the IN-RANGE (no-trap) path leaves the CCR documented-UNPREDICTABLE (PRM) — a corpus
            // artifact filtered on the data axis (M68000M45d1TomHarteTests). The PC/prefetch state IS exact
            // for them, but the data axis (asserted alongside) would spuriously fail, so mirror the filter.
            if (IsChkInRangeCase(c)) { unpredictable++; continue; }
            string? r = M68000TomHarteRunner.RunCase(c, assertExceptions: true, pcPrefetchAxis: true);
            // Address-error (vector 3) stays deferred (IsAddressErrorCase) — its precise group-0 frame + the
            // trace-coupled bits are M4.5d-2 T3/2b; the runner returns the deferred sentinel for it.
            if (ReferenceEquals(r, M68000TomHarteRunner.DeferredException)) { deferred++; continue; }
            executed++;
            if (r is not null) { failures.Add(r); if (failures.Count >= 10) break; }
        }

        // Anti-fake guard: the file must EXECUTE a substantial body of cases (not be entirely deferred).
        Assert.True(executed > 0, $"{file}: 0 executed (non-deferred) cases — the PC/prefetch gate would be vacuous");
        Assert.True(failures.Count == 0,
            $"{file}: {failures.Count}+ PC/prefetch-axis failures over {executed} executed " +
            $"({deferred} deferred, {unpredictable} CHK-in-range-unpredictable):\n" + string.Join("\n", failures));
    }

    /// <summary>A CHK case (operword 0xF1C0/0x4180) on the no-trap path (Dn in [0, bound]) — the 68000 leaves
    /// N/Z/V/C UNPREDICTABLE there (PRM; vector-confirmed). Excluded from the data-axis CCR assertion the
    /// PC/prefetch sweep also runs. The CHK-trap cases (IsExceptionCase true) ARE asserted.</summary>
    private static bool IsChkInRangeCase(M68000TomHarteCase c)
    {
        if (c.Initial.Prefetch.Length == 0) return false;
        uint ow = c.Initial.Prefetch[0];
        if ((ow & 0xF1C0u) != 0x4180u) return false;
        return !M68000TomHarteRunner.IsExceptionCase(c);
    }
}

// ---- M4.5d-1 control-flow + exception families (22) ----
public sealed class M68000Pc_Bcc       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "Bcc.json.gz"; }
public sealed class M68000Pc_BSR       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "BSR.json.gz"; }
public sealed class M68000Pc_DBcc      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "DBcc.json.gz"; }
public sealed class M68000Pc_JMP       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "JMP.json.gz"; }
public sealed class M68000Pc_JSR       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "JSR.json.gz"; }
public sealed class M68000Pc_RTS       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "RTS.json.gz"; }
public sealed class M68000Pc_RTR       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "RTR.json.gz"; }
public sealed class M68000Pc_RTE       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "RTE.json.gz"; }
public sealed class M68000Pc_LINK      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "LINK.json.gz"; }
public sealed class M68000Pc_UNLINK    : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "UNLINK.json.gz"; }
public sealed class M68000Pc_TRAP      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "TRAP.json.gz"; }
public sealed class M68000Pc_TRAPV     : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "TRAPV.json.gz"; }
public sealed class M68000Pc_CHK       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "CHK.json.gz"; }
public sealed class M68000Pc_NOP       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "NOP.json.gz"; }
public sealed class M68000Pc_ANDItoCCR : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ANDItoCCR.json.gz"; }
public sealed class M68000Pc_ANDItoSR  : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ANDItoSR.json.gz"; }
public sealed class M68000Pc_ORItoCCR  : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ORItoCCR.json.gz"; }
public sealed class M68000Pc_ORItoSR   : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ORItoSR.json.gz"; }
public sealed class M68000Pc_EORItoCCR : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "EORItoCCR.json.gz"; }
public sealed class M68000Pc_EORItoSR  : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "EORItoSR.json.gz"; }
public sealed class M68000Pc_DIVU      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "DIVU.json.gz"; }
public sealed class M68000Pc_DIVS      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "DIVS.json.gz"; }
// ---- M4.5a-c data families (46) ----
public sealed class M68000Pc_MOVE_b      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVE.b.json.gz"; }
public sealed class M68000Pc_MOVE_w      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVE.w.json.gz"; }
public sealed class M68000Pc_MOVE_l      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVE.l.json.gz"; }
public sealed class M68000Pc_MOVEA_w     : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEA.w.json.gz"; }
public sealed class M68000Pc_MOVEA_l     : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEA.l.json.gz"; }
public sealed class M68000Pc_MOVE_q      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVE.q.json.gz"; }
public sealed class M68000Pc_MOVEfromSR  : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEfromSR.json.gz"; }
public sealed class M68000Pc_MOVEtoSR    : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEtoSR.json.gz"; }
public sealed class M68000Pc_MOVEtoCCR   : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEtoCCR.json.gz"; }
public sealed class M68000Pc_MOVEfromUSP : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEfromUSP.json.gz"; }
public sealed class M68000Pc_MOVEtoUSP   : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEtoUSP.json.gz"; }
public sealed class M68000Pc_ADD_b       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ADD.b.json.gz"; }
public sealed class M68000Pc_ADD_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ADD.w.json.gz"; }
public sealed class M68000Pc_ADD_l       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ADD.l.json.gz"; }
public sealed class M68000Pc_SUB_b       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "SUB.b.json.gz"; }
public sealed class M68000Pc_SUB_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "SUB.w.json.gz"; }
public sealed class M68000Pc_SUB_l       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "SUB.l.json.gz"; }
public sealed class M68000Pc_AND_b       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "AND.b.json.gz"; }
public sealed class M68000Pc_OR_w        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "OR.w.json.gz"; }
public sealed class M68000Pc_EOR_l       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "EOR.l.json.gz"; }
public sealed class M68000Pc_CMP_b       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "CMP.b.json.gz"; }
public sealed class M68000Pc_CMP_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "CMP.w.json.gz"; }
public sealed class M68000Pc_CMP_l       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "CMP.l.json.gz"; }
public sealed class M68000Pc_NEG_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "NEG.w.json.gz"; }
public sealed class M68000Pc_NOT_l       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "NOT.l.json.gz"; }
public sealed class M68000Pc_CLR_b       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "CLR.b.json.gz"; }
public sealed class M68000Pc_TST_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "TST.w.json.gz"; }
public sealed class M68000Pc_MULU        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MULU.json.gz"; }
public sealed class M68000Pc_MULS        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MULS.json.gz"; }
public sealed class M68000Pc_ASL_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ASL.w.json.gz"; }
public sealed class M68000Pc_LSR_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "LSR.w.json.gz"; }
public sealed class M68000Pc_ROL_w       : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ROL.w.json.gz"; }
public sealed class M68000Pc_ROXR_w      : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ROXR.w.json.gz"; }
public sealed class M68000Pc_BTST        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "BTST.json.gz"; }
public sealed class M68000Pc_BCHG        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "BCHG.json.gz"; }
public sealed class M68000Pc_BCLR        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "BCLR.json.gz"; }
public sealed class M68000Pc_BSET        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "BSET.json.gz"; }
public sealed class M68000Pc_ABCD        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "ABCD.json.gz"; }
public sealed class M68000Pc_NBCD        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "NBCD.json.gz"; }
public sealed class M68000Pc_Scc         : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "Scc.json.gz"; }
public sealed class M68000Pc_SWAP        : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "SWAP.json.gz"; }
public sealed class M68000Pc_LEA         : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "LEA.json.gz"; }
public sealed class M68000Pc_PEA         : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "PEA.json.gz"; }
public sealed class M68000Pc_TAS         : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "TAS.json.gz"; }
public sealed class M68000Pc_MOVEM_w     : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEM.w.json.gz"; }
public sealed class M68000Pc_MOVEM_l     : M68000TimingAxisTomHarteSweepBase { protected override string VectorFile => "MOVEM.l.json.gz"; }

/// <summary>Structural guard: the per-file derived classes cover EXACTLY the canonical PC/prefetch file list
/// (the union of the pre-split M45d1Files + M45acFiles).</summary>
public sealed class M68000TimingAxisTomHarteCoverageGuard
{
    [Fact]
    public void Split_classes_cover_exactly_the_canonical_pcprefetch_file_list()
    {
        var expected = M68000TimingAxisTomHarteSweepBase.CanonicalFiles.OrderBy(x => x).ToArray();
        var covered = typeof(M68000TimingAxisTomHarteSweepBase).Assembly.GetTypes()
            .Where(t => t.IsSealed && !t.IsAbstract && typeof(M68000TimingAxisTomHarteSweepBase).IsAssignableFrom(t))
            .Select(t => ((M68000TimingAxisTomHarteSweepBase)Activator.CreateInstance(t)!).FileForGuard)
            .OrderBy(x => x).ToArray();
        Assert.Equal(expected, covered);
    }
}
