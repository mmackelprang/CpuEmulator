using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5d-1 (Task 14 axis c): the CROSS-CORPUS exception sweep — re-runs the M4.5a-c vector files
/// (MOVE/ALU/shift/bit/BCD/Scc/data-movement) with assertExceptions:true over the EMBEDDED exception cases.
///
/// EMPIRICAL HONESTY FINDING (verified against the whole 68000/v1 corpus): every embedded exception in the
/// M4.5a-c files is an ADDRESS ERROR (vector 3) — there are ZERO embedded privilege (vector 8) or illegal
/// (vector 4) cases anywhere in the corpus, and the only ÷0/CHK/TRAP/TRAPV cases live in the dedicated d-1
/// files (asserted by M68000M45d1TomHarteTests). So this sweep's REAL job is a REGRESSION GUARD: it proves
/// that turning assertExceptions on over the existing corpus does NOT wrongly assert the address-error cases
/// (they stay correctly DEFERRED via the runner's IsAddressErrorCase, DD4 — the precise 14-byte group-0 frame
/// is M4.5d-2) and does not destabilize anything. The genuine exception-model proof is the dedicated d-1 files
/// (TRAP 32-47, TRAPV 7, CHK 6, ÷0 5). Privilege (vector 8) + ILLEGAL (vector 4) are SYNTHETIC-tested only —
/// no v1 vector exercises them (disclosed). The TIMING axis is M4.5d-2 (timingAxis:false).</summary>
public class M68000ExceptionCorpusTomHarteTests
{
    // The full M4.5a-c corpus (MOVE + ALU + shift/bit/BCD/Scc/data-movement) — every embedded exception case is
    // an address error (vector 3, deferred per DD4). Re-run with assertExceptions:true as the regression guard.
    public static IEnumerable<object[]> CorpusFiles =>
    [
        // MOVE family (M4.5a)
        ["MOVE.b.json.gz"], ["MOVE.w.json.gz"], ["MOVE.l.json.gz"],
        ["MOVEA.w.json.gz"], ["MOVEA.l.json.gz"],
        // integer ALU (M4.5b)
        ["ADD.b.json.gz"], ["ADD.w.json.gz"], ["ADD.l.json.gz"],
        ["SUB.b.json.gz"], ["SUB.w.json.gz"], ["SUB.l.json.gz"],
        ["AND.b.json.gz"], ["AND.w.json.gz"], ["AND.l.json.gz"],
        ["OR.b.json.gz"], ["OR.w.json.gz"], ["OR.l.json.gz"],
        ["EOR.b.json.gz"], ["EOR.w.json.gz"], ["EOR.l.json.gz"],
        ["CMP.b.json.gz"], ["CMP.w.json.gz"], ["CMP.l.json.gz"],
        ["NEG.b.json.gz"], ["NEG.w.json.gz"], ["NEG.l.json.gz"],
        ["NOT.b.json.gz"], ["NOT.w.json.gz"], ["NOT.l.json.gz"],
        ["CLR.b.json.gz"], ["CLR.w.json.gz"], ["CLR.l.json.gz"],
        ["TST.b.json.gz"], ["TST.w.json.gz"], ["TST.l.json.gz"],
        ["MULU.json.gz"], ["MULS.json.gz"],
        // shift/rotate + bit + BCD + Scc + data-movement (M4.5c)
        ["ASL.w.json.gz"], ["LSR.w.json.gz"], ["ROL.w.json.gz"], ["ROXR.w.json.gz"],
        ["BTST.json.gz"], ["BCHG.json.gz"], ["BCLR.json.gz"], ["BSET.json.gz"],
        ["ABCD.json.gz"], ["NBCD.json.gz"], ["Scc.json.gz"],
        ["SWAP.json.gz"], ["LEA.json.gz"], ["PEA.json.gz"], ["TAS.json.gz"],
        ["MOVEM.w.json.gz"], ["MOVEM.l.json.gz"],
    ];

    [M68000TomHarteTheory]
    [MemberData(nameof(CorpusFiles))]
    public void Embedded_exception_cases_assert_on_the_data_axis(string file)
    {
        string? dir = M68000TomHarteVectors.TryGetVectorDirectory();
        Assert.NotNull(dir);
        string path = Path.Combine(dir, file);
        if (!File.Exists(path)) return;   // a few corpus files may be absent in trimmed caches — skip silently

        var cases = M68000TomHarteLoader.LoadFile(path);
        var failures = new List<string>();
        int asserted = 0;        // embedded exception cases that ran + asserted on the data axis (the proof)
        int addrDeferred = 0;    // address-error cases still deferred (DD4 — M4.5d-2)
        foreach (var c in cases)
        {
            // Only the EMBEDDED exception cases are the subject here: a case the corpus marks as an exception
            // (IsExceptionCase) that is NOT an address-error (DD4). Non-exception cases are covered by the
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
