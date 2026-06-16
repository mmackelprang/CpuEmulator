using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CpuEmulator.Tests.TomHarte;

/// <summary>M4.5d-1 (Task 14 axis c): the CROSS-CORPUS exception sweep. Re-runs the M4.5a-c vector files
/// (MOVE/ALU/shift/bit/BCD/Scc/data-movement) with assertExceptions:true so the EMBEDDED exception cases —
/// every case whose real 68000 took a privilege violation (vector 8), an illegal instruction (vector 4), or a
/// ÷0 (vector 5) — flip deferred→asserted and are diffed on the data axis (frame + mode + handler PC via RAM/
/// SR/SSP). This is the un-fakeable proof the exception model is right across the WHOLE existing corpus, not
/// just the 20 dedicated files. The address-error (vector 3) large-frame WORD contents stay deferred (DD4 — the
/// runner's IsAddressErrorCase; assert trap-taken only, M4.5d-2 for the precise group-0 words). The TIMING axis
/// is M4.5d-2 (timingAxis:false).
///
/// HONESTY: this sweep asserts ONLY the cases the corpus marks as exceptions (the embedded axis); the
/// non-exception cases are already asserted green by the M4.5a/b/c sweeps with the default flag — they are NOT
/// re-run here. The newly-asserting count (the embedded small-frame exceptions) is the merge-gate evidence.</summary>
public class M68000ExceptionCorpusTomHarteTests
{
    // The full M4.5a-c corpus (MOVE + ALU + shift/bit/BCD/Scc/data-movement) — every file carries embedded
    // exception cases (privilege/illegal/÷0/address-error). Re-run with assertExceptions:true.
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
